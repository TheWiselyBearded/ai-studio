# Exposing the AI Studio API via Cloudflare Tunnel

## What is Cloudflare Tunnel?

Cloudflare Tunnel (`cloudflared`) creates a secure outbound connection from your machine to Cloudflare's edge network. External devices hit a Cloudflare URL, and Cloudflare routes the traffic through the tunnel to your local Flask server. No port forwarding, no firewall changes, no static IP needed.

Your localhost endpoints continue to work exactly as before — the tunnel just adds a public URL on top.

## Prerequisites

**Install `cloudflared`** (the CLI tool). No Python packages are needed.

### Option A: Chocolatey (recommended)
```powershell
choco install cloudflared
```

### Option B: Direct download
1. Go to https://github.com/cloudflare/cloudflared/releases
2. Download `cloudflared-windows-amd64.exe`
3. Rename to `cloudflared.exe` and place it somewhere on your PATH (e.g., `C:\Tools\`)

### Verify installation
```powershell
cloudflared --version
```

## Quick Tunnel (No Account Required)

This is the fastest way to get a public URL. No Cloudflare account, no DNS setup.

**Terminal 1 — Start the server:**
```bash
python run_comfy_server.py --port 5001
```

**Terminal 2 — Start the tunnel:**
```bash
cloudflared tunnel --url http://localhost:5001
```

`cloudflared` will print a URL like:
```
https://abc123-something.trycloudflare.com
```

Share that URL. Any device on the internet can now hit your API:
```bash
curl https://abc123-something.trycloudflare.com/voices
curl -X POST https://abc123-something.trycloudflare.com/generate/image \
  -H "Content-Type: application/json" \
  -d '{"prompt": "A red sports car"}'
```

**Limitations of quick tunnels:**
- URL is random and changes every time you restart `cloudflared`
- No authentication built in
- Fine for testing and short-term sharing

## Using the `--tunnel` Flag (Auto-Launch)

Instead of running `cloudflared` in a separate terminal, use the built-in flag:

```bash
python run_comfy_server.py --port 5001 --tunnel
```

This automatically:
1. Spawns `cloudflared` as a background process
2. Detects and prints the public tunnel URL
3. Cleans up the tunnel when the server stops

The tunnel URL will appear in the console output shortly after startup.

## Persistent Tunnel (Stable URL)

For a permanent URL that doesn't change between restarts, set up a named tunnel with a Cloudflare account.

### 1. Create a free Cloudflare account
Go to https://dash.cloudflare.com/sign-up — no credit card required.

### 2. Authenticate
```bash
cloudflared tunnel login
```
This opens a browser. Select your Cloudflare account and authorize.

### 3. Create a named tunnel
```bash
cloudflared tunnel create ai-studio
```

### 4. Configure the tunnel
Create `~/.cloudflared/config.yml`:
```yaml
tunnel: ai-studio
credentials-file: C:\Users\<you>\.cloudflared\<tunnel-id>.json

ingress:
  - hostname: ai-studio.yourdomain.com
    service: http://localhost:5001
  - service: http_status:404
```

### 5. Route DNS
```bash
cloudflared tunnel route dns ai-studio ai-studio.yourdomain.com
```

### 6. Run
```bash
cloudflared tunnel run ai-studio
```

Now `https://ai-studio.yourdomain.com` always points to your local server (as long as `cloudflared` is running).

## API Endpoint Reference

Once your tunnel is active, replace `localhost:5001` with your tunnel URL.

| Method | Endpoint                       | Content-Type          | Description                    |
|--------|--------------------------------|-----------------------|--------------------------------|
| POST   | `/generate/3d`                 | multipart/form-data   | 3D model from image            |
| POST   | `/generate/audio`              | application/json      | Voice design TTS               |
| POST   | `/generate/image`              | application/json      | Image generation               |
| POST   | `/generate/voice-clone`        | multipart/form-data   | Clone a voice from audio       |
| POST   | `/generate/voice-clone-speech` | multipart/form-data   | Speech with cloned voice       |
| GET    | `/voices`                      | —                     | List available cloned voices   |

### Example: Generate an image from another device
```bash
TUNNEL=https://abc123-something.trycloudflare.com

curl -X POST "$TUNNEL/generate/image" \
  -H "Content-Type: application/json" \
  -d '{"prompt": "A futuristic city skyline", "width": 1024, "height": 1024}' \
  --output generated.png
```

### Example: Generate audio
```bash
curl -X POST "$TUNNEL/generate/audio" \
  -H "Content-Type: application/json" \
  -d '{"text": "Hello from the cloud!", "character": "Female", "style": "Warm"}' \
  --output speech.wav
```

### Example: Upload image for 3D
```bash
curl -X POST "$TUNNEL/generate/3d" \
  -F "image=@my_model.png" \
  --output model.glb
```

## Cloudflare Tunnel vs ngrok

| Feature          | Cloudflare Tunnel          | ngrok (free tier)              |
|------------------|----------------------------|--------------------------------|
| Bandwidth        | Unlimited                  | Limited (~1 GB/month)          |
| Connections      | Unlimited                  | 40 connections/minute          |
| Cost             | Free                       | Free (with limits)             |
| Custom domains   | Free (with Cloudflare DNS) | Paid plans only                |
| Account required | No (quick tunnel)          | Yes (for most features)        |
| Stable URL       | Yes (named tunnel)         | Yes (paid plan)                |
| Setup complexity | Low                        | Low                            |

**Recommendation:** Cloudflare Tunnel — no bandwidth caps matters for sending GLB files, images, and audio back and forth.

## Security Notes

- Quick tunnels are **publicly accessible** — anyone with the URL can hit your API
- For production or sensitive use, add authentication via Cloudflare Access (free for up to 50 users) or add API key validation to the Flask app
- The tunnel encrypts traffic end-to-end (HTTPS)
