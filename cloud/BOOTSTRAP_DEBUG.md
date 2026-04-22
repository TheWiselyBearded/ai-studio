# Lambda Cloud bootstrap — field notes

Live notebook from running `cloud/bootstrap.sh` against a real Lambda Cloud
instance for the first time. Each section records one observed problem, its
root cause, and the fix that was applied (either to the running instance or to
this repo).

**Target:** `gpu_1x_a10` in `us-east-1`, instance id `baa3b41abc494327ab1dde906aaeab0d`, IP `129.153.163.161`, launched 2026-04-22 ~19:11 UTC.

Final state after fixes: tunnel `https://kidney-cement-rio-rand.trycloudflare.com`, `/healthz` + `/voices` (with auth header) return 200. All 7 bootstrap phases complete; `comfy_trellis` was skipped for this first run.

---

## Issue 1 — `user_data` failed immediately: `curl: (22) 404`

### Symptom
`/var/log/ai-studio-user-data.log`:

```
curl: (22) The requested URL returned error: 404
chmod: cannot access '/tmp/bootstrap.sh': No such file or directory
bash: /tmp/bootstrap.sh: No such file or directory
```

### Root cause
The repo at `github.com/TheWiselyBearded/ai-studio` is **private**.
`user_data_template.sh` calls `curl -fsSL https://raw.githubusercontent.com/.../cloud/bootstrap.sh`,
and GitHub returns 404 to anonymous clients on private content (not 403 — it
denies existence entirely, which is indistinguishable from a typo).

### Applied fix for this session
Skipped the URL fetch: scp'd `cloud/bootstrap.sh`, `cloud/fs_init.sh`, and
`ai-studio-light.zip` from the developer workstation to `/tmp/` on the
instance, then ran bootstrap with `AI_STUDIO_BUNDLE_URL=file:///tmp/ai-studio-light.zip`.

### Permanent fix options
1. **Make the repo public** — fastest. Confirm no historical blob contains a
   secret first (`git log -p | grep -iE 'api_key|secret'`).
2. **GitHub Personal Access Token** — add `GITHUB_TOKEN` to the Unity launch
   env, render it into `user_data_template.sh` as `curl -H "Authorization: Bearer $GITHUB_TOKEN"`.
3. **scp path** — have Unity's launch flow scp the `cloud/` directory + bundle
   to `/tmp/` over the registered SSH key before kicking off bootstrap. Adds
   a ~26 MB pre-flight, but works today.

---

## Issue 2 — `ai-studio.service` restart-looping: `ModuleNotFoundError: No module named 'websocket'`

### Symptom
`/var/log/ai-studio.log` was a wall of:

```
Traceback (most recent call last):
  File "/opt/ai-studio/run_comfy_server.py", line 5, in <module>
    import websocket  # pip install websocket-client
ModuleNotFoundError: No module named 'websocket'
```

### Root cause
`bootstrap.sh` installed ComfyUI's own `requirements.txt` into `comfy_main`
but never installed the AI Studio Flask server's runtime deps
(`flask`, `websocket-client`, `python-dotenv`, `requests`), which are
listed in [README.md](../README.md) but weren't scripted.

### Fix (applied in repo — [cloud/bootstrap.sh](bootstrap.sh), `install_comfy_main`)

```bash
run_as_user "source '$MINIFORGE_DIR/etc/profile.d/conda.sh' && conda activate $env_name && \
  pip install flask websocket-client python-dotenv requests"
```

### Applied live on instance

```bash
source ~/miniforge3/etc/profile.d/conda.sh && conda activate comfy_main
pip install flask websocket-client python-dotenv requests
sudo systemctl restart ai-studio
```

---

## Issue 3 — `comfy-main.service` failing: `OSError: libcudart.so.13: cannot open shared object file`

### Symptom
`journalctl -u comfy-main`:

```
File ".../torchaudio/_extension/utils.py", line 56, in _load_lib
    torch.ops.load_library(paths[0])
OSError: libcudart.so.13: cannot open shared object file: No such file or directory
```

### Root cause
The bootstrap installs the CU121 torch trio **first**, then runs
`pip install -r comfy_dir/requirements.txt` and then `pip install -U --pre comfyui-manager`.
One of those pulls `torchaudio` from PyPI (not from the CU121 index) and
silently **upgrades** torchaudio from `2.5.1+cu121` to `2.11.0`, which is
linked against CUDA 13's `libcudart.so.13`. `torch` itself stayed at
`2.5.1+cu121`, so the mismatch tripped at extension load time.

### Fix (applied in repo — [cloud/bootstrap.sh](bootstrap.sh))

Add a `--force-reinstall --no-deps` re-pin of the torch trio from the CU121
index **after** all other pip work in each comfy_main install block.

```bash
pip install --force-reinstall --no-deps torch torchvision torchaudio \
    --index-url https://download.pytorch.org/whl/cu121
```

This was already present in `install_comfy_hunyuan` but was missing from
`install_comfy_main`. That's why only comfy_main had the mismatch.

### Applied live on instance

```bash
source ~/miniforge3/etc/profile.d/conda.sh && conda activate comfy_main
pip install --force-reinstall --no-deps torch torchvision torchaudio \
    --index-url https://download.pytorch.org/whl/cu121
sudo systemctl restart comfy-main
```

---

## Issue 4 — Cloudflare tunnel URL never written: Python print() buffering under systemd

### Symptom
`ai-studio.service` was running, `cloudflared` was running, but
`/var/ai-studio/tunnel.url` stayed empty and the tunnel-watch systemd unit
never saw the URL line. The only `[Tunnel]` line in `/var/log/ai-studio.log`
was the static `Starting Cloudflare quick tunnel...` that's emitted before
the reader thread starts. Manually running `cloudflared tunnel --url http://localhost:5001`
produced the URL in ~3 seconds, confirming cloudflared itself was fine.

### Root cause
`run_comfy_server.py._start_cloudflare_tunnel` spawns a thread that reads
cloudflared's stderr and calls `print(f"[Tunnel] {text}")`. Under systemd
(`StandardOutput=append:/var/log/ai-studio.log`), stdout is **not a TTY**,
so Python defaults to block buffering. Werkzeug's own log lines made it
through because Werkzeug auto-flushes; our background-thread prints sat in
a 4 KB buffer and never reached the file, so the tunnel-watch `grep` never
matched.

### Fix (applied in repo)
Two independent mitigations, both landed:

1. **[run_comfy_server.py](../run_comfy_server.py)** — pass `flush=True` to
   every `print()` in the tunnel reader thread.
2. **[cloud/bootstrap.sh](bootstrap.sh)** — the `ai-studio.service` unit now
   sets `Environment=PYTHONUNBUFFERED=1`, which is belt-and-suspenders for
   the same issue.

### Applied live on instance

```bash
sudo sed -i '/^Environment=AI_STUDIO_TOKEN=/a Environment=PYTHONUNBUFFERED=1' \
    /etc/systemd/system/ai-studio.service
sudo systemctl daemon-reload
sudo systemctl restart ai-studio ai-studio-tunnel-watch
```

---

## Issue 5 — Cloudflare returned 404 for every path on the quick tunnel URL

### Symptom
Tunnel URL landed in `/var/ai-studio/tunnel.url` (`https://site-self-affecting-vacuum.trycloudflare.com`),
the Flask server answered `/healthz` on `127.0.0.1:5001` correctly, but
`curl https://<tunnel>/healthz` returned:

```
HTTP/1.1 404 Not Found
Server: cloudflare
CF-Ray: 9f0715047dbe8ea3-LAX
```

### Root cause
Lambda's GPU Ubuntu image ships with `cloudflared` pre-installed and a
pre-populated `/etc/cloudflared/config.yml` for their Jupyter
tunnel:

```yaml
tunnel: 2db5b19a-9152-41c0-8edd-b38093834d6e
credentials-file: /etc/cloudflared/jupyter-tunnel.json
ingress:
  - hostname: f8639fc9f5384d9696c52096e715f7d0-0.lambdaspaces.com
    service: http://localhost:7000
  - service: http_status:404
```

When we invoke `cloudflared tunnel --url http://localhost:5001`, cloudflared
**merges** the default config with the command-line flags. The
`credentials-file` gets picked up, the quick-tunnel URL gets allocated, but
the `http_status:404` ingress rule applies to every non-matching hostname —
which is every request to our trycloudflare.com URL.

Settings line confirming the merge was in `/var/log/ai-studio.log`:

```
Settings: map[cred-file:/etc/cloudflared/jupyter-tunnel.json ... url:http://localhost:5001]
```

### Fix (applied in repo — [run_comfy_server.py](../run_comfy_server.py) `_start_cloudflare_tunnel`)

Add `--config /dev/null` (via `os.devnull` for cross-platform) and
`--no-autoupdate` to the cloudflared invocation so it ignores the default
config file.

```python
proc = subprocess.Popen(
    ["cloudflared", "tunnel", "--no-autoupdate",
     "--config", os.devnull,
     "--url", f"http://localhost:{port}"],
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
)
```

### Applied live on instance
scp'd the updated `run_comfy_server.py` to `/opt/ai-studio/`, then
`sudo systemctl restart ai-studio ai-studio-tunnel-watch`. A fresh quick
tunnel URL appeared (`https://kidney-cement-rio-rand.trycloudflare.com`) and
`/healthz` returned 200 end-to-end.

---

## Issue 6 — `models/` never became a symlink to the persistent FS

### Symptom
After bootstrap completed, `ls -la /home/ubuntu/Tools/ComfyUI_Main/models/`
showed a real directory with empty subdirs (`checkpoints/`, `clip/`, `vae/`,
…), not a symlink to `/opt/ai-studio-models/`. That meant image/3D/audio
pipelines couldn't find any weights loaded onto the persistent filesystem.

### Root cause
Phase 5 of `bootstrap.sh` skipped the symlink whenever `models/` was
non-empty. ComfyUI's Git repository checks in a placeholder `models/`
directory with empty subdirs (one for each weight category). The "empty-dir"
probe `[ -z "$(ls -A "$comfy_dir/models")" ]` returned false for a tree full
of empty folders, so the symlink step was skipped.

### Fix (applied in repo — [cloud/bootstrap.sh](bootstrap.sh) `phase_models_link`)

Treat "all files are zero bytes OR no files at all" as "placeholder tree; safe
to nuke". Specifically, if `find "$dir/models" -type f -size +0c` returns no
results, remove the directory and create the symlink.

```bash
real_files="$(find "$comfy_dir/models" -type f -size +0c 2>/dev/null | head -n1)"
if [ -z "$real_files" ]; then
    rm -rf "$comfy_dir/models"
    ln -s "$FS_MOUNT" "$comfy_dir/models"
fi
```

(Also re-points an existing symlink to the current `$FS_MOUNT` so re-running
bootstrap after attaching a different FS works cleanly.)

### Applied live on instance
Not required for this instance — the running session isn't using a real FS
yet (`fs_init.sh` hasn't been run to populate weights). When the user runs
**Initialize Models Filesystem** from Unity or reruns bootstrap against a
pristine instance, the new Phase 5 logic will take effect.

---

## Issue 7 — `ai-studio-light.zip` bundle was stale

### Symptom
After fixing the websocket-client install, `ai-studio.service` still failed
with `run_comfy_server.py: error: unrecognized arguments: --auth-token`. The
zipped bundle predates the Phase 0 `--auth-token` flag.

### Root cause
`ai-studio-light.zip` on disk was built on 2026-03-23 before the Lambda
integration work. Every run_comfy_server.py change needs a rebuild before
it's published as a release asset.

### Fix
Two things:

1. **For this session:** scp the current repo's `run_comfy_server.py` over
   the instance's `/opt/ai-studio/run_comfy_server.py` directly.
2. **Long-term:** rebuild `ai-studio-light.zip` from current HEAD before
   each release cut. Include at minimum: `run_comfy_server.py`,
   `run_comfy_api.py`, every `*.json` workflow, `README.md`, and
   `convert_dinov3_pth_to_hf.py`. The `v0.1.0` release currently published
   has the stale bundle — re-attach before anyone else launches.

---

## Summary of fixes landed in the repo

| File | Change |
|------|--------|
| [cloud/bootstrap.sh](bootstrap.sh) | Install Flask server runtime deps; force-reinstall CU121 torch trio after every pip step that can bump torchaudio; Phase 5 treats empty-subdir trees as placeholder; `ai-studio.service` gets `PYTHONUNBUFFERED=1` |
| [run_comfy_server.py](../run_comfy_server.py) | `cloudflared` invocation uses `--config os.devnull --no-autoupdate`; `print(..., flush=True)` in tunnel reader thread |

## Still open, not fixed in code yet

- **Private-repo 404** — decide between making repo public, shipping a GitHub
  PAT, or having Unity scp the bundle. Until this lands, `user_data` still
  dies on the first curl.
- **Rebuild `ai-studio-light.zip`** from current HEAD and re-attach to the
  `v0.1.0` release.
- **Model weights** — bootstrap and service infra are now correct, but the
  weights filesystem still needs `fs_init.sh` to run once per region.

## How to launch clean from this state

If the running `baa3b41a…` instance is still live when this doc is read, you
can point Unity at it directly:

1. **AI Studio → Settings**
2. Active Mode → **Remote**
3. Remote Base URL → `https://kidney-cement-rio-rand.trycloudflare.com` (or
   whatever `cat /var/ai-studio/tunnel.url` reports if the service has
   restarted since)
4. Auth Token → `709451d34bc94997cf08f7d8c4bacdf34f23e3bfe4f0b70b2f1bf5566bc046e2`

Image/3D/audio generation will return 400 until weights land in
`/opt/ai-studio-models/` (or a real persistent FS). Tunnel, auth, and
endpoint dispatch are all healthy as of 2026-04-22 19:37 UTC.
