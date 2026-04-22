# AI Studio

AI-powered 3D asset generation and audio synthesis platform. Uses ComfyUI as the generation backend and provides REST API, CLI, and Unity Editor interfaces.

**Prerequisites:** ComfyUI must be running at `127.0.0.1:8001` before using any commands.

## Installation

```bash
pip install flask websocket-client
```

## Audio Generation (`run_comfy_api.py`)

### CLI Mode

```bash
# Generate audio with default test text
python run_comfy_api.py --cli

# Generate audio with custom text
python run_comfy_api.py --cli --text "The quick brown fox jumps over the lazy dog"
```

| Argument | Type    | Default                                    | Description                       |
|----------|---------|--------------------------------------------|-----------------------------------|
| `--cli`  | flag    | `False`                                    | Run in CLI mode instead of server |
| `--text` | string  | `"This is a default test from the CLI."`   | Text to synthesize                |
| `--port` | integer | `5000`                                     | Port for the API server           |

**Output:** FLAC audio file saved to the current directory.

### API Server Mode

```bash
# Start the audio API server (default port 5000)
python run_comfy_api.py

# Start on a custom port
python run_comfy_api.py --port 8080
```

#### `POST /generate`

Generate audio from text.

**Request:**
```bash
curl -X POST http://127.0.0.1:5000/generate \
  -H "Content-Type: application/json" \
  -d '{"text": "Hello world"}'
```

**Response:** Audio file (binary, FLAC format).

**Errors:**
| Status | Reason                  |
|--------|-------------------------|
| 400    | Missing `text` field    |
| 500    | ComfyUI or generation failure |

---

## 3D Model Generation (`run_comfy_3d.py`)

### CLI Mode

```bash
# Generate from a single front view (duplicated for front and back)
python run_comfy_3d.py --cli --images front.png

# Generate from two views
python run_comfy_3d.py --cli --images front.png back.png

# Generate from all four views
python run_comfy_3d.py --cli --images front.png back.png left.png right.png
```

| Argument   | Type       | Default | Description                                        |
|------------|------------|---------|----------------------------------------------------|
| `--cli`    | flag       | `False` | Run in CLI mode instead of server                  |
| `--images` | file paths | —       | 1–4 image paths (order: front, back, left, right)  |
| `--port`   | integer    | `5001`  | Port for the API server                            |

**Supported image formats:** PNG, JPG, JPEG, BMP, TGA, TIFF

**Output:** GLB file saved to `./results/`.

### API Server Mode

```bash
# Start the 3D generation API server (default port 5001)
python run_comfy_3d.py

# Start on a custom port
python run_comfy_3d.py --port 9000
```

#### `POST /generate`

Generate a 3D GLB model from 1–4 multi-view images.

**Option 1 — Named view fields (recommended):**
```bash
curl -X POST http://127.0.0.1:5001/generate \
  -F "front=@image_front.png" \
  -F "back=@image_back.png" \
  -F "left=@image_left.png" \
  -F "right=@image_right.png"
```

**Option 2 — Generic images field:**
```bash
curl -X POST http://127.0.0.1:5001/generate \
  -F "images=@front.png" \
  -F "images=@back.png" \
  -F "images=@left.png" \
  -F "images=@right.png"
```

| Field   | Required | Description       |
|---------|----------|-------------------|
| `front` | Yes      | Front view image  |
| `back`  | No       | Back view image   |
| `left`  | No       | Left view image   |
| `right` | No       | Right view image  |

**Response:** GLB file (binary).

**Errors:**
| Status | Reason                        |
|--------|-------------------------------|
| 400    | No images provided            |
| 500    | ComfyUI or generation failure |

---

## Unity Editor Integration

The Unity project is located in `Unity_AI_Studio/AI_Studio_Engine/` (Unity 6000.3.6f1).

### Hunyuan 3D Generator Window

**Open:** Menu bar > **AI Studio > Hunyuan 3D Generator**

**Usage:**
1. Add 1–4 images via **Browse** button or drag-and-drop into the Front, Back, Left, Right slots.
2. Configure **API URL** (default: `http://127.0.0.1:5001/generate`) and **Output Folder** (default: `Assets/Generated3D`).
3. Click **Generate 3D Model**.
4. The generated GLB is automatically imported and selected in the Project window.

**Timeout:** 10 minutes per generation.

---

## Configuration

### ComfyUI Server

Both Python scripts connect to ComfyUI at:
```
127.0.0.1:8001
```

This is set via `server_address` at the top of each script.

### Audio Workflow (`Scratch_Voice_design.json`)

Key parameters:

| Parameter            | Default   | Description                 |
|----------------------|-----------|-----------------------------|
| `model_size`         | `1.7B`   | TTS model size              |
| `device`             | `auto`   | Compute device              |
| `precision`          | `bf16`   | Model precision             |
| `language`           | `Auto`   | Language selection           |
| `temperature`        | `0.9`    | Sampling temperature        |
| `top_p`              | `0.9`    | Nucleus sampling threshold  |
| `top_k`              | `50`     | Top-k sampling              |
| `repetition_penalty` | `1.0`    | Repetition penalty          |
| `character`          | `Female` | Voice character             |
| `style`              | `Warm`   | Voice style                 |

### 3D Workflow (`scratch_text3D.json`)

Key parameters:

| Parameter          | Default        | Description                  |
|--------------------|----------------|------------------------------|
| `steps`            | `20`           | Diffusion sampling steps     |
| `cfg`              | `7.5`          | Classifier-free guidance     |
| `sampler_name`     | `euler`        | Sampling algorithm           |
| `scheduler`        | `normal`       | Noise scheduler              |
| `octree_resolution`| `256`          | Voxel resolution             |
| `num_chunks`       | `8000`         | VAE decode chunks            |
| `algorithm`        | `surface net`  | Mesh extraction algorithm    |
| `threshold`        | `0.6`          | Mesh extraction threshold    |
| `model`            | `hunyuan3d-dit-v2-mv_fp16.safetensors` | Model checkpoint |

---

## Project Structure

```
ai_studio/
├── run_comfy_api.py                 # Audio generation API & CLI
├── run_comfy_3d.py                  # 3D model generation API & CLI
├── Scratch_Voice_design.json        # Audio workflow (ComfyUI)
├── scratch_text3D.json              # 3D workflow (ComfyUI)
├── input/                           # Input files
├── results/                         # Generated GLB output
└── Unity_AI_Studio/
    └── AI_Studio_Engine/            # Unity 6 project
        └── Assets/
            └── Editor/
                └── Hunyuan3DGeneratorWindow.cs
```
