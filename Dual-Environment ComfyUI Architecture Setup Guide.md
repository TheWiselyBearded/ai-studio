# Multi-Environment ComfyUI Architecture Setup Guide

**Author:** abahrema

**Objective:** Establish isolated ComfyUI environments (`ComfyUI_Hunyuan` for Hunyuan 3D, `ComfyUI_Main` for general workflows/audio, and `ComfyUI_Trellis` for Trellis2 3D mesh generation) sharing a single central models folder.

---

## Part 1: Shared Resource Configuration

To prevent duplicating gigabytes of model files, both environments pull from a central Desktop folder.

**Target Directory:** `C:\Users\abahrema\Desktop\models\`

**Configuration Method** (Applied to both environments):

1. Renamed `extra_model_paths.yaml.example` to `extra_model_paths.yaml`.
2. Edited the file to point to the shared directory:

```yaml
comfyui:
    base_path: C:/Users/abahrema/Desktop/models/
    checkpoints: checkpoints/
    clip: clip/
    vae: vae/
    loras: loras/
```

---

## Part 2: Environment 1 — ComfyUI_Hunyuan (The 3D Quarantine Zone)

**Purpose:** Dedicated strictly to Hunyuan 3D 2.1 generation, isolating heavy C++ renderers (`nvdiffrast`) from general updates.

### Phase 1: Base Build

```bash
cd C:\Users\abahrema\Documents\Tools
git clone https://github.com/comfyanonymous/ComfyUI.git ComfyUI_Hunyuan
conda create -n comfy_hunyuan python=3.10 -y
conda activate comfy_hunyuan
cd ComfyUI_Hunyuan
conda install pytorch torchvision torchaudio pytorch-cuda=12.1 -c pytorch -c nvidia -y
pip install -r requirements.txt
```

### Phase 2: Node Installation & Dependency Slicing

```bash
cd custom_nodes
git clone https://github.com/visualbruno/ComfyUI-Hunyuan3d-2-1
cd ComfyUI-Hunyuan3d-2-1

# Batch 1: Core
pip install trimesh pyhocon addict hydra-core loguru diffusers hydra-zen scikit-image plyfile pymeshlab pytorch-lightning

# Batch 2: Vision Fixes
pip install opencv-python-headless onnxruntime-gpu "rembg[gpu]" ninja

# Batch 3: 3D Mesh
pip install xatlas yacs pytorch-msssim pygltflib timm torchtyping meshlib ffmpeg-python

# Batch 4: Hardware
pip install accelerate -U pybind11

# Batch 5: Build Isolation Bypass
pip install git+https://github.com/NVlabs/nvdiffrast/ --no-build-isolation
pip install torch-scatter --no-build-isolation
```

### Phase 3: Compiling C++ Renderers

```bash
cd hy3dpaint\custom_rasterizer
python setup.py install
cd ..\DifferentiableRenderer
python setup.py install
```

### Phase 4: Troubleshooting & Hotfixes

1. **Missing Standard Nodes:**
   - Cloned `ltdrdata/ComfyUI-Manager` into `custom_nodes`.
   - Used Manager UI to install missing `INTConstant` and `StringConstant` nodes.

2. **`torch.load` Security Vulnerability (CVE-2025-32434):**
   - **Symptom:** `transformers` blocked loading `.bin` files without PyTorch 2.6.
   - **Fix:** Lobotomized the security check. Opened `...\envs\comfy_hunyuan\lib\site-packages\transformers\utils\import_utils.py` and replaced the contents of `def check_torch_load_is_safe():` with `pass`.

3. **`FileNotFoundError` (`_metallic.jpg`):**
   - **Symptom:** Workflow crashed compiling the GLB mesh.
   - **Fix:** Added text (e.g., `drone_test`) to the empty `output_mesh_name` field in the Hunyuan 3D 2.1 InPaint node.

### Phase 5: The Launcher (`Launch_Hunyuan.bat`)

Saved to Desktop:

```bat
@echo off
echo Starting Hunyuan Quarantine Zone...
call conda activate comfy_hunyuan
cd /d C:\Users\abahrema\Documents\Tools\ComfyUI_Hunyuan
python main.py --port 8001
pause
```

---

## Part 3: Environment 2 — ComfyUI_Main (Daily Driver & Audio Engine)

**Purpose:** Standard environment for 2D generation and cutting-edge audio/TTS workflows, utilizing the new ComfyUI V2 native package manager.

### Phase 1: Base Build

```bash
cd C:\Users\abahrema\Documents\Tools
git clone https://github.com/comfyanonymous/ComfyUI.git ComfyUI_Main
conda create -n comfy_main python=3.10 -y
conda activate comfy_main
cd ComfyUI_Main
conda install pytorch torchvision torchaudio pytorch-cuda=12.1 -c pytorch -c nvidia -y
pip install -r requirements.txt
```

### Phase 2: Native Manager Integration

```bash
# V2 UI requires native pip install, not git clone
pip install -U --pre comfyui-manager
```

### Phase 3: The Launcher (`Launch_Main.bat`)

Saved to Desktop (note the `--enable-manager` flag):

```bat
@echo off
echo Starting ComfyUI Main Workspace...
call conda activate comfy_main
cd /d C:\Users\abahrema\Documents\Tools\ComfyUI_Main
python main.py --enable-manager --port 8000
pause
```

### Phase 4: Node Installation (1038lab QwenTTS)

Because the node was unlisted in the Manager, it required manual injection:

```bash
cd C:\Users\abahrema\Documents\Tools\ComfyUI_Main\custom_nodes
git clone https://github.com/1038lab/ComfyUI-QwenTTS.git
cd ComfyUI-QwenTTS
pip install -r requirements.txt
```

### Phase 5: Troubleshooting & Hotfixes

1. **CUDA Wipeout:**
   - **Symptom:** The node's `requirements.txt` blindly downgraded PyTorch to a CPU-only version (`AssertionError: Torch not compiled with CUDA enabled`).
   - **Fix:** Nuke and reinstall GPU wheels:
     ```bash
     pip uninstall torch torchvision torchaudio -y
     pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121
     ```

2. **Obsolete PyTorch Decorators:**
   - **Symptom:** Silent crash (unknown location import error) due to outdated `transformers` decorator syntax in hardcoded node logic.
   - **Fix:** Commented out `from transformers.utils.generic import check_model_inputs` and all instances of `@check_model_inputs()` in `...\custom_nodes\ComfyUI-QwenTTS\qwen_tts\core\tokenizer_12hz\modeling_qwen3_tts_tokenizer_v2.py`.

3. **HuggingFace Config Typo (`pad_token_id`):**
   - **Symptom:** Model crashed searching for `pad_token_id`.
   - **Fix:** Opened `config.json` for both VoiceDesign and Base models and manually added `"pad_token_id": 2150,` beneath the `"codec_pad_id"` line.

4. **RoPE Initialization Failure (`KeyError: 'default'`):**
   - **Symptom:** The node failed to initialize Rotary Positional Embeddings because `transformers>=5.0.0` dropped support for the `"default"` key.
   - **Fix:** Downgraded the isolated environment to the specific `transformers` version the model was originally built against:
     ```bash
     conda activate comfy_main
     pip install transformers==4.57.3
     ```
     (Ensured `config.json` files remained set to `"type": "default"`.)

---

## Part 4: Environment 3 — ComfyUI_Trellis (Trellis2 3D Mesh Generation)

**Purpose:** Dedicated to [ComfyUI-Trellis2](https://github.com/visualbruno/ComfyUI-Trellis2) 3D mesh generation. Requires Python 3.11 and Torch 2.7+ with CUDA 12.8, which is incompatible with the Hunyuan environment (Python 3.10, CUDA 12.1).

### Phase 1: Base Build

```bash
cd C:\Users\abahrema\Documents\Tools
git clone https://github.com/comfyanonymous/ComfyUI.git ComfyUI_Trellis
conda create -n comfy_trellis python=3.11 -y
conda activate comfy_trellis
cd ComfyUI_Trellis
pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu128
pip install -r requirements.txt
```

### Phase 2: Node Installation

```bash
cd custom_nodes
git clone https://github.com/visualbruno/ComfyUI-Trellis2.git
cd ComfyUI-Trellis2

# Prebuilt wheels (Torch 2.7 / Python 3.11)
pip install wheels/Windows/Torch270/cumesh-1.0-cp311-cp311-win_amd64.whl
pip install wheels/Windows/Torch270/nvdiffrast-0.4.0-cp311-cp311-win_amd64.whl
pip install wheels/Windows/Torch270/nvdiffrec_render-0.0.0-cp311-cp311-win_amd64.whl
pip install wheels/Windows/Torch270/flex_gemm-0.0.1-cp311-cp311-win_amd64.whl
pip install wheels/Windows/Torch270/o_voxel-0.0.1-cp311-cp311-win_amd64.whl

# Pip requirements
pip install meshlib requests pymeshlab opencv-python scipy open3d plotly rembg
```

### Phase 3: DINOv3 Model

Trellis2 requires Facebook DINOv3. Clone into the ComfyUI models directory:

```bash
cd C:\Users\abahrema\Documents\Tools\ComfyUI_Trellis\models
mkdir facebook
cd facebook
git clone https://huggingface.co/facebook/dinov3-vitl16-pretrain-lvd1689m
```

### Phase 4: Shared Models & Launcher

Copied `extra_model_paths.yaml` pointing to the shared `C:/Users/abahrema/Desktop/models/` directory.

```bat
@echo off
echo Starting Trellis2 3D Generation...
call conda activate comfy_trellis
cd /d C:\Users\abahrema\Documents\Tools\ComfyUI_Trellis
python main.py --port 8002
pause
```

### Port Mapping (All Environments)

| Environment | Port | Purpose |
|------------|------|---------|
| `comfy_main` | 8000 | 2D/Audio workflows |
| `comfy_hunyuan` | 8001 | Hunyuan 3D generation |
| `comfy_trellis` | 8002 | Trellis2 3D mesh generation |
