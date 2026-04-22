RuntimeError: qwen_tts is not available. Please install dependencies in your ComfyUI environment and check the package path. Import error: cannot import name 'Qwen3TTSTokenizerV1Config' from 'qwen_tts.core' (unknown location)


  File "C:\Users\abahrema\AppData\Local\Programs\ComfyUI\resources\ComfyUI\execution.py", line 524, in execute
    output_data, output_ui, has_subgraph, has_pending_tasks = await get_output_data(prompt_id, unique_id, obj, input_data_all, execution_block_cb=execution_block_cb, pre_execute_cb=pre_execute_cb, v3_data=v3_data)
                                                              ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

  File "C:\Users\abahrema\AppData\Local\Programs\ComfyUI\resources\ComfyUI\execution.py", line 333, in get_output_data
    return_values = await _async_map_node_over_list(prompt_id, unique_id, obj, input_data_all, obj.FUNCTION, allow_interrupt=True, execution_block_cb=execution_block_cb, pre_execute_cb=pre_execute_cb, v3_data=v3_data)
                    ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

  File "C:\Users\abahrema\AppData\Local\Programs\ComfyUI\resources\ComfyUI\execution.py", line 307, in _async_map_node_over_list
    await process_inputs(input_dict, i)

  File "C:\Users\abahrema\AppData\Local\Programs\ComfyUI\resources\ComfyUI\execution.py", line 295, in process_inputs
    result = f(**inputs)
             ^^^^^^^^^^^

  File "C:\Users\abahrema\Documents\Tools\ComfyUI\custom_nodes\ComfyUI-QwenTTS\AILab_QwenTTS.py", line 893, in generate
    return _voice_design_generate(
           ^^^^^^^^^^^^^^^^^^^^^^^

  File "C:\Users\abahrema\Documents\Tools\ComfyUI\custom_nodes\ComfyUI-QwenTTS\AILab_QwenTTS.py", line 620, in _voice_design_generate
    model = _load_model("VoiceDesign", model_size, device, precision, attention)
            ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

  File "C:\Users\abahrema\Documents\Tools\ComfyUI\custom_nodes\ComfyUI-QwenTTS\AILab_QwenTTS.py", line 382, in _load_model
    raise RuntimeError(
