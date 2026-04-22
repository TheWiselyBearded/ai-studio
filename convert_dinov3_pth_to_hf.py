"""
Convert DINOv3 ViT-L/16 .pth checkpoint to HuggingFace Transformers format.

Usage:
    conda activate comfy_trellis
    python convert_dinov3_pth_to_hf.py
"""

import os
import shutil
import torch
from transformers import DINOv3ViTConfig, DINOv3ViTModel

PTH_PATH = r"C:\Users\abahrema\Documents\Tools\ComfyUI_Trellis\models\facebook\dinov3-vitl16-pretrain-lvd1689m\dinov3_vitl16_pretrain_lvd1689m-8aa4cbdd.pth"
OUTPUT_DIR = r"C:\Users\abahrema\Documents\Tools\ComfyUI_Trellis\models\facebook\dinov3-vitl16-pretrain-lvd1689m"


def convert():
    print("Loading .pth checkpoint...")
    pth_sd = torch.load(PTH_PATH, map_location="cpu", weights_only=False)

    # ViT-L/16 config matching the .pth architecture
    config = DINOv3ViTConfig(
        hidden_size=1024,
        num_hidden_layers=24,
        num_attention_heads=16,
        intermediate_size=4096,
        patch_size=16,
        image_size=224,
        num_channels=3,
        num_register_tokens=4,
        key_bias=False,
        query_bias=False,
        value_bias=False,
        apply_layernorm=True,
        layerscale_value=1.0,
        mlp_bias=True,
        proj_bias=True,
    )

    hf_sd = {}

    for pth_key, tensor in pth_sd.items():
        # Skip keys that don't exist in HF model
        if pth_key == "rope_embed.periods":
            continue
        if pth_key.endswith(".attn.qkv.bias_mask"):
            continue
        if pth_key.endswith(".attn.qkv.bias"):
            # All bias_masks are zero, so skip bias entirely
            continue

        # Embeddings
        if pth_key == "cls_token":
            hf_sd["embeddings.cls_token"] = tensor
        elif pth_key == "storage_tokens":
            hf_sd["embeddings.register_tokens"] = tensor
        elif pth_key == "mask_token":
            # PTH: [1, 1024] -> HF: [1, 1, 1024]
            hf_sd["embeddings.mask_token"] = tensor.unsqueeze(0)
        elif pth_key.startswith("patch_embed.proj."):
            suffix = pth_key.split("patch_embed.proj.")[-1]
            hf_sd[f"embeddings.patch_embeddings.{suffix}"] = tensor

        # Transformer blocks
        elif pth_key.startswith("blocks."):
            parts = pth_key.split(".")
            block_idx = parts[1]

            rest = ".".join(parts[2:])

            if rest.startswith("norm1.") or rest.startswith("norm2."):
                hf_sd[f"layer.{block_idx}.{rest}"] = tensor

            elif rest == "attn.qkv.weight":
                # Split fused QKV [3*hidden, hidden] -> 3x [hidden, hidden]
                q, k, v = tensor.chunk(3, dim=0)
                hf_sd[f"layer.{block_idx}.attention.q_proj.weight"] = q
                hf_sd[f"layer.{block_idx}.attention.k_proj.weight"] = k
                hf_sd[f"layer.{block_idx}.attention.v_proj.weight"] = v

            elif rest == "attn.proj.weight":
                hf_sd[f"layer.{block_idx}.attention.o_proj.weight"] = tensor
            elif rest == "attn.proj.bias":
                hf_sd[f"layer.{block_idx}.attention.o_proj.bias"] = tensor

            elif rest == "ls1.gamma":
                hf_sd[f"layer.{block_idx}.layer_scale1.lambda1"] = tensor
            elif rest == "ls2.gamma":
                hf_sd[f"layer.{block_idx}.layer_scale2.lambda1"] = tensor

            elif rest.startswith("mlp.fc1."):
                suffix = rest.split("mlp.fc1.")[-1]
                hf_sd[f"layer.{block_idx}.mlp.up_proj.{suffix}"] = tensor
            elif rest.startswith("mlp.fc2."):
                suffix = rest.split("mlp.fc2.")[-1]
                hf_sd[f"layer.{block_idx}.mlp.down_proj.{suffix}"] = tensor

            else:
                print(f"  WARNING: unmapped key: {pth_key}")

        # Top-level norm
        elif pth_key.startswith("norm."):
            hf_sd[pth_key] = tensor

        else:
            print(f"  WARNING: unmapped key: {pth_key}")

    # Instantiate model and load weights
    print(f"Created {len(hf_sd)} HF keys from {len(pth_sd)} PTH keys")
    print("Instantiating DINOv3ViTModel...")
    model = DINOv3ViTModel(config)

    expected_keys = set(model.state_dict().keys())
    converted_keys = set(hf_sd.keys())

    missing = expected_keys - converted_keys
    extra = converted_keys - expected_keys

    if missing:
        print(f"  Missing keys ({len(missing)}): {missing}")
    if extra:
        print(f"  Extra keys ({len(extra)}): {extra}")

    model.load_state_dict(hf_sd, strict=True)
    print("State dict loaded successfully (strict=True)")

    # Save in HuggingFace format
    print(f"Saving to {OUTPUT_DIR}...")
    model.save_pretrained(OUTPUT_DIR)
    print("Done! Files created:")
    for f in os.listdir(OUTPUT_DIR):
        size = os.path.getsize(os.path.join(OUTPUT_DIR, f))
        print(f"  {f} ({size / 1e6:.1f} MB)" if size > 1e6 else f"  {f}")


if __name__ == "__main__":
    convert()
