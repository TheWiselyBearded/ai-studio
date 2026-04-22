# ComfyUI 3D Node Installation & Troubleshooting Guide
**Purpose:** Actionable master instructions for resolving deep dependency conflicts and compiling custom C++ nodes in ComfyUI (specifically for heavy 3D pipelines like `ComfyUI-Hunyuan3d-2-1` and `ComfyUI-3D-Pack`).

## Phase 1: The "Bulletproof" Execution Rule
When dealing with isolated ComfyUI Desktop App installations, standard `activate.bat` scripts often fail or install packages to the wrong Windows environment. 
* **The Rule:** NEVER use generic `pip install` commands.
* **The Action:** Always force installations directly into the ComfyUI virtual environment by using the absolute path to its Python executable.
* **Format:** `[PATH_TO_COMFY]\.venv\Scripts\python.exe -m pip install [package]`

## Phase 2: Resolving the "Dependency Onion"
Heavy 3D nodes often fail to list all required packages in their `requirements.txt`. Install these missing modules in batches using the bulletproof command above:

1. **Core Math & Utilities:** `trimesh pyhocon addict hydra-core loguru diffusers hydra-zen scikit-image plyfile pymeshlab pytorch-lightning`
2. **Vision & Rendering Fixes:**
   * `opencv-python-headless` *(Crucial: Install the headless version to override conflicting `cv2` shims from other nodes)*
   * `onnxruntime-gpu "rembg[gpu]" ninja` *(Crucial: Install the GPU version of rembg. If missing, rembg will instantly crash the entire ComfyUI server during startup instead of skipping the node)*
3. **3D Mesh & Formatting:** `xatlas yacs pytorch-msssim pygltflib timm torchtyping meshlib ffmpeg-python`
4. **Hardware Offloading:** `accelerate -U` *(Required for `enable_model_cpu_offload` functions)*
5. **C++ Compiling Prerequisites:** `pybind11` *(Must be installed before attempting Phase 4)*

## Phase 3: Bypassing "Build Isolation" Panics
Certain heavy C++ libraries (`nvdiffrast`, `torch-scatter`) will fail to install because `pip` builds them in an isolated temporary environment where they cannot detect the local PyTorch installation.
* **The Fix:** Append `--no-build-isolation` to force the compiler to look at the existing environment.
* **Actions:**
  * `...\python.exe -m pip install git+https://github.com/NVlabs/nvdiffrast/ --no-build-isolation`
  * `...\python.exe -m pip install torch-scatter --no-build-isolation`

## Phase 4: Manual C++ Node Compilation
Some nodes (like Hunyuan3D) rely on custom CUDA/C++ renderers that cannot be downloaded via pip; they must be compiled locally using the machine's GPU toolkits.

**Action 1: Compile the Custom Rasterizer**
1. Navigate to the source code: `cd [PATH_TO_COMFY]\custom_nodes\ComfyUI-Hunyuan3d-2-1\hy3dpaint\custom_rasterizer`
2. Execute the build: `...\.venv\Scripts\python.exe setup.py install`

**Action 2: Compile the Differentiable Renderer**
1. Navigate to the source code: `cd [PATH_TO_COMFY]\custom_nodes\ComfyUI-Hunyuan3d-2-1\hy3dpaint\DifferentiableRenderer`
2. Execute the build: `...\.venv\Scripts\python.exe setup.py install`
*(Note: If this fails with a missing `pybind11` error, revert to Phase 2, step 5).*

## Phase 5: Workflow & UI Quirks
Even with perfect dependencies, workflows will fail if configured improperly.

1. **Workflow Selection for Image-to-3D:** * Do NOT use `Mesh_Texturing.json` (it expects an already-finished `.glb` file and will throw a `ValueError: string is not a file` if given an image folder).
   * **Action:** Always use the master `Full_Workflow.json` to process the entire pipeline (Image -> Multi-view -> Mesh -> UV Unwrap -> Bake -> Export).
2. **Missing Utility Nodes (Red Boxes):**
   * If template workflows load with red `INTConstant`, `SetNode`, or `GetNode` boxes, they are missing UI-organization extensions.
   * **Action:** Do not manually hunt for these. Use the **ComfyUI Manager -> Install Missing Custom Nodes** button to automatically fetch the required custom scripts pack.
3. **The Node Isolation Trick:**
   * If massive node packs (e.g., ComfyUI-3D-Pack and comfyui-motioncapture) are conflicting and crashing the server during startup, you do not need to delete them.
   * **Action:** Rename their root folder to end in `.disabled` (e.g., `ComfyUI-3D-Pack.disabled`). ComfyUI will completely ignore them on the next boot, allowing you to isolate and test specific nodes.