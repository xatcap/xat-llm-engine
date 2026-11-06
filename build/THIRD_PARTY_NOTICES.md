# Third-party notices

xat-llm-engine uses the MIT License (see `LICENSE`). Its release packages also include or attach the software below. The full license and notice texts are in the `licenses/` folder of each package.

| Component | Where it appears | License | Texts in the package |
|---|---|---|---|
| llama.cpp (build b12110) | The `llama/` folder | MIT | `llama/LICENSE` and `licenses/llama.cpp/LICENSE` |
| cpp-httplib | Inside llama.cpp | MIT | `licenses/llama.cpp/cpp-httplib.txt` |
| nlohmann/json | Inside llama.cpp | MIT | `licenses/llama.cpp/nlohmann-json.txt` |
| miniaudio | Inside llama.cpp | Public domain or MIT-0 | `licenses/llama.cpp/miniaudio.txt` |
| stb_image | Inside llama.cpp | MIT or public domain | `licenses/llama.cpp/stb_image.txt` |
| sheredom/subprocess | Inside llama.cpp | Unlicense | `licenses/llama.cpp/sheredom-subprocess.txt` |
| SHA-1 in C | Inside llama.cpp | Public domain | `licenses/llama.cpp/sha1.txt` |
| SHA-256 (Igor Pavlov) | Inside llama.cpp | Public domain | `licenses/llama.cpp/sha256.txt` |
| xxHash | Inside llama.cpp | BSD-2-Clause | `licenses/llama.cpp/xxhash.txt` |
| rotate-bits | Inside llama.cpp | MIT | `licenses/llama.cpp/rotate-bits.txt` |
| The llama.cpp web UI | Inside llama-server. The engine turns it off with `--no-webui`. | MIT, with the npm packages in `tools/ui/package-lock.json` of llama.cpp b12110 | `licenses/llama.cpp/LICENSE` |
| .NET runtime | Inside the engine program | MIT, with third-party notices | `licenses/dotnet/Microsoft.NETCore.App.Runtime/` |
| ASP.NET Core | Inside the engine program | MIT, with third-party notices | `licenses/dotnet/Microsoft.AspNetCore.App.Runtime/` |
| Microsoft.AspNetCore.OpenApi | Inside the engine program | MIT | `licenses/nuget/Microsoft.AspNetCore.OpenApi/` |
| Microsoft.OpenApi | Inside the engine program | MIT | `licenses/nuget/Microsoft.OpenApi/` |
| Swashbuckle.AspNetCore.SwaggerUI | Inside the engine program. The engine serves it only in the Development environment. | MIT | `licenses/nuget/Swashbuckle.AspNetCore.SwaggerUI/` |
| swagger-ui 5.33.3 | Inside Swashbuckle.AspNetCore.SwaggerUI | Apache-2.0, with a NOTICE file | `licenses/nuget/Swashbuckle.AspNetCore.SwaggerUI/` |
| Ministral 3 3B Instruct (GGUF Q4_K_M) by Mistral AI | A separate release file | Apache-2.0 | The release file `Ministral-LICENSE.txt` |

The release includes the model without changes. Users must not use it in a way that infringes third-party rights, as stated on its model card.

`build/third-party.json` lists the same components for the release script. `python3 build/release.py check-notices` fails when a package that the engine ships is missing from that list.
