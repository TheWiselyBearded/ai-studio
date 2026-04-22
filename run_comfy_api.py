import json
import urllib.request
import urllib.parse
import uuid
import websocket # pip install websocket-client
import argparse
import os
from flask import Flask, request, jsonify, send_file # pip install Flask

# 1. Configuration
server_address = "127.0.0.1:8000"
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

# --- Helper Functions ---
def queue_prompt(prompt_workflow, client_id):
    p = {"prompt": prompt_workflow, "client_id": client_id}
    data = json.dumps(p).encode('utf-8')
    req = urllib.request.Request(f"http://{server_address}/prompt", data=data)
    response = urllib.request.urlopen(req)
    return json.loads(response.read())

def get_history(prompt_id):
    with urllib.request.urlopen(f"http://{server_address}/history/{prompt_id}") as response:
        return json.loads(response.read())

def get_file(filename, subfolder, folder_type):
    data = {"filename": filename, "subfolder": subfolder, "type": folder_type}
    url_values = urllib.parse.urlencode(data)
    with urllib.request.urlopen(f"http://{server_address}/view?{url_values}") as response:
        return response.read()
# ------------------------

# --- Core Generation Logic ---
def generate_audio(text_input, character="Female", style="Warm"):
    """Handles the entire ComfyUI pipeline and returns the path to the saved audio file."""
    client_id = str(uuid.uuid4()) # Generate fresh ID for each run

    with open(os.path.join(SCRIPT_DIR, "Scratch_Voice_design.json"), "r", encoding="utf-8") as f:
        workflow_data = json.load(f)

    # Inject programmatic text
    target_node_id = "15"
    workflow_data[target_node_id]["inputs"]["text"] = text_input

    # Inject voice character and style into the Voice Instruct node
    voice_instruct_node_id = "10"
    workflow_data[voice_instruct_node_id]["inputs"]["character"] = character
    workflow_data[voice_instruct_node_id]["inputs"]["style"] = style

    print("Connecting to ComfyUI...")
    ws = websocket.WebSocket()
    ws.connect(f"ws://{server_address}/ws?clientId={client_id}")

    print(f"Queueing prompt for text: '{text_input[:30]}...'")
    queue_response = queue_prompt(workflow_data, client_id)
    prompt_id = queue_response['prompt_id']
    print(f"Prompt queued! ID: {prompt_id}")

    print("Generating audio... (waiting for ComfyUI to finish)")
    while True:
        out = ws.recv()
        if isinstance(out, str):
            message = json.loads(out)
            if message['type'] == 'executing':
                data = message['data']
                if data['node'] is None and data['prompt_id'] == prompt_id:
                    print("Execution complete!")
                    break
    ws.close()

    history = get_history(prompt_id)[prompt_id]
    save_node_id = "11" 

    if save_node_id in history['outputs']:
        output_data = history['outputs'][save_node_id]
        if 'audio' in output_data:
            audio_info = output_data['audio'][0] 
            filename = audio_info['filename']
            subfolder = audio_info['subfolder']
            folder_type = audio_info['type']
            
            print(f"Downloading {filename} from ComfyUI...")
            audio_bytes = get_file(filename, subfolder, folder_type)
            
            clean_filename = filename.split('/')[-1] if '/' in filename else filename
            
            with open(clean_filename, "wb") as f:
                f.write(audio_bytes)
            print(f"Success! Audio saved locally as: {clean_filename}")
            
            # Return the absolute path so the API knows exactly where it is
            return os.path.abspath(clean_filename)
            
    raise Exception("Audio generation failed or no audio was found in Node 11 output.")

# --- Flask API Setup ---
app = Flask(__name__)

@app.route('/generate', methods=['POST'])
def api_generate():
    data = request.json
    if not data or 'text' not in data:
        return jsonify({"error": "Missing 'text' in JSON payload"}), 400

    try:
        # Run the pipeline with optional character/style overrides
        character = data.get('character', 'Female')
        style = data.get('style', 'Warm')
        output_filepath = generate_audio(data['text'], character=character, style=style)
        
        # Send the actual audio file back in the API response!
        return send_file(output_filepath, as_attachment=True)
    
    except Exception as e:
        return jsonify({"error": str(e)}), 500

# --- Entry Point (CLI vs Server logic) ---
if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="ComfyUI Audio Generator (API & CLI)")
    parser.add_argument("--cli", action="store_true", help="Run in CLI mode instead of starting the server")
    parser.add_argument("--text", type=str, default="This is a default test from the CLI.", help="Text to generate when using --cli")
    parser.add_argument("--character", type=str, default="Female", help="Voice character (Female, Male)")
    parser.add_argument("--style", type=str, default="Warm", help="Voice style (Warm, Bright, Calm, Energetic, Soft, Deep)")
    parser.add_argument("--port", type=int, default=5000, help="Port to run the API server on")
    parser.add_argument("--comfy-server", type=str, default=None, help="ComfyUI server address (default: 127.0.0.1:8000)")

    args = parser.parse_args()

    if args.comfy_server:
        server_address = args.comfy_server

    if args.cli:
        print("Running in CLI mode...")
        generate_audio(args.text, character=args.character, style=args.style)
    else:
        print(f"Starting Audio API Server on http://127.0.0.1:{args.port}...")
        print(f"ComfyUI server: {server_address}")
        print("Use CTRL+C to stop.")
        # Runs the Flask dev server
        app.run(host="0.0.0.0", port=args.port)