This folder holds the model files of the engine. The engine runs two models.

1. The query-expansion model. It is already in this folder.
   File: Qwen3-0.6B-Q4_K_M.gguf

2. The main model, for chat. Put it in this folder.
   File: Ministral-3-3B-Instruct-2512-Q4_K_M.gguf
   Where to get it: the same GitHub release as this package.
   https://github.com/xatcap/xat-llm-engine/releases
   Check it against SHA256SUMS from the release, then restart the engine.

Without the main model file, the state of the main model is "missing". Chat requests return 502.
Without the query-expansion file, the state of that model is "missing". Requests to /v1/query-expansion return 502.
To turn a model off, set LocalModel:Enabled or QueryExpansionModel:Enabled to false.
