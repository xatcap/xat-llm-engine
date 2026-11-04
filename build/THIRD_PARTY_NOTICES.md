# Third-party notices

xat-llm-engine is released under the MIT License (see `LICENSE`). Its release packages also include or attach the following software.

| Component | Where it appears | License | Source |
|---|---|---|---|
| llama.cpp (build b12110) | The `llama/` folder | MIT, full text in `llama/LICENSE` | https://github.com/ggml-org/llama.cpp |
| Ministral 3 3B Instruct (GGUF Q4_K_M) by Mistral AI | A separate release file | Apache-2.0, full text in the release file `Ministral-LICENSE.txt` | https://huggingface.co/mistralai/Ministral-3-3B-Instruct-2512-GGUF |
| .NET runtime and ASP.NET Core | Inside the engine program | MIT | https://github.com/dotnet/runtime and https://github.com/dotnet/aspnetcore |
| Microsoft.Extensions.Http.Resilience and Microsoft.OpenApi | Inside the engine program | MIT | https://github.com/dotnet/extensions and https://github.com/microsoft/OpenAPI.NET |
| Polly | Inside the engine program | BSD-3-Clause | https://github.com/App-vNext/Polly |
| Swashbuckle.AspNetCore.SwaggerUI | Inside the engine program. It is served only in the Development environment | MIT | https://github.com/domaindrivendev/Swashbuckle.AspNetCore |
| swagger-ui | Inside Swashbuckle.AspNetCore.SwaggerUI | Apache-2.0 | https://github.com/swagger-api/swagger-ui |

The model is distributed unmodified. Users must not use it in a way that infringes third-party rights, as stated on its model card.
