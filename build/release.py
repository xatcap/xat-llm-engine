#!/usr/bin/env python3
"""Builds, tests and prepares release files. It uses only the standard library, so it runs the same way on every runner.

Commands:
  package     Builds the engine package for one platform, with the pinned llama.cpp build inside.
  fetch-model Downloads the pinned model and checks its hash.
  smoke-test  Runs a built package with the real model, and sends real chat requests.
  finalize    Checks the size limit and writes SHA256SUMS for a folder of release files.
  check-changelog Checks that CHANGELOG.md has a section for a version, and can write that section as release notes.
  check-notices   Checks that build/third-party.json lists every package that the engine ships, with its license files.
"""
import argparse
import contextlib
import hashlib
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BUILD = ROOT / "build"
HOST_PROJECT = ROOT / "src" / "XatLlmEngine.Host" / "XatLlmEngine.Host.csproj"
CHANGELOG = ROOT / "CHANGELOG.md"
LICENSES = BUILD / "licenses"
HOST_LOCK = ROOT / "src" / "XatLlmEngine.Host" / "packages.lock.json"
HOST_ASSETS = ROOT / "src" / "XatLlmEngine.Host" / "obj" / "project.assets.json"
PROGRAM = "XatLlmEngine.Host"
GITHUB_ASSET_LIMIT = 2 * 1024**3  # GitHub requires each release file to be under 2 GiB.


def load_lock(name):
    return json.loads((BUILD / name).read_text(encoding="utf-8"))


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as file:
        while chunk := file.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def download_verified(url, destination, expected_sha256):
    """Downloads a file unless a verified copy already exists. A file that fails the check is never kept."""
    destination = Path(destination)
    if destination.exists() and sha256_of(destination) == expected_sha256:
        print(f"Using verified copy: {destination}")
        return destination

    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_name(destination.name + ".part")
    print(f"Downloading {url}")
    request = urllib.request.Request(url, headers={"User-Agent": "xat-llm-engine-release"})
    # The timeout applies to each read, so a download that receives no data fails but a slow download does not.
    with urllib.request.urlopen(request, timeout=120) as response, open(partial, "wb") as file:
        shutil.copyfileobj(response, file, 1024 * 1024)

    actual = sha256_of(partial)
    if actual != expected_sha256:
        partial.unlink()
        sys.exit(f"Hash mismatch for {url}: expected {expected_sha256}, got {actual}")

    partial.replace(destination)
    return destination


def extract(archive, destination):
    archive = str(archive)
    if archive.endswith(".zip"):
        with zipfile.ZipFile(archive) as bundle:
            bundle.extractall(destination)
    else:
        with tarfile.open(archive) as bundle:
            # The "data" filter refuses paths that would escape the destination folder.
            bundle.extractall(destination, filter="data")


def server_file_name(rid):
    return "llama-server.exe" if rid.startswith("win") else "llama-server"


def program_file_name(rid):
    return f"{PROGRAM}.exe" if rid.startswith("win") else PROGRAM


def clean_tar_entry(entry):
    """Removes the owner from the build machine. If it stays, root restores it when root unpacks the archive."""
    entry.uid = entry.gid = 0
    entry.uname = entry.gname = ""
    return entry


def stop_engine(engine, rid):
    """Stops the engine. Windows has no gentle signal here, so this stops the whole process tree, which also frees the files."""
    if rid.startswith("win"):
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(engine.pid)], check=False)
    else:
        engine.terminate()


@contextlib.contextmanager
def work_folder(parent):
    """Gives a temporary folder and removes it after use. The folder is in the cached model folder, and the cache must not include it."""
    work = tempfile.mkdtemp(dir=parent)
    try:
        yield work
    finally:
        remove_folder(Path(work))


def remove_folder(folder, attempts=30):
    """Removes a folder. It retries, because Windows does not delete the files of a program until that program has fully stopped."""
    for attempt in range(1, attempts + 1):
        try:
            shutil.rmtree(folder)
        except PermissionError as error:
            if attempt == attempts:
                raise
            print(f"Could not remove the folder at attempt {attempt}, and will try again: {error}", flush=True)
            time.sleep(1)
        else:
            return


def version_argument(value):
    """Accepts only plain version text, because the value becomes part of file paths and build arguments."""
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?", value):
        raise argparse.ArgumentTypeError(f"{value!r} is not a version such as 1.2.3 or 1.2.3-rc.1")
    return value


def package(args):
    rid = args.rid
    lock = load_lock("llama-cpp.lock.json")
    asset = lock["assets"][rid]
    output = Path(args.output).resolve()
    cache = output / "cache"
    url = f"https://github.com/{lock['repository']}/releases/download/{lock['tag']}/{asset['name']}"
    llama_archive = download_verified(url, cache / asset["name"], asset["sha256"])

    name = f"xat-llm-engine-{args.version}-{rid}"
    stage = output / "stage" / name
    shutil.rmtree(stage, ignore_errors=True)
    stage.mkdir(parents=True)

    # NuGet refuses a locked restore with --runtime when the lock file holds several platforms. RuntimeIdentifiers in the
    # projects already restores the packages of each platform, so a locked restore comes first, and the publish does not restore.
    subprocess.run([os.environ.get("DOTNET", "dotnet"), "restore", str(HOST_PROJECT), "--locked-mode"], check=True, cwd=ROOT)
    subprocess.run(
        [
            os.environ.get("DOTNET", "dotnet"), "publish", str(HOST_PROJECT),
            "--no-restore",
            "--configuration", "Release",
            "--runtime", rid,
            "--self-contained", "true",
            "--output", str(stage),
            "-p:DebugType=none",
            f"-p:Version={args.version}",
        ],
        check=True,
        cwd=ROOT,
    )

    # These publish outputs are for developers and are not needed to run the engine.
    for leftover in [*stage.glob("*.xml"), *stage.glob("*.staticwebassets.*")]:
        leftover.unlink()

    with tempfile.TemporaryDirectory(dir=output) as unpacked:
        extract(llama_archive, unpacked)
        servers = list(Path(unpacked).rglob(server_file_name(rid)))
        if len(servers) != 1:
            sys.exit(f"Expected one {server_file_name(rid)} in {asset['name']}, found {len(servers)}")
        shutil.copytree(servers[0].parent, stage / "llama", symlinks=True)

    models = stage / "models"
    models.mkdir()
    shutil.copy(BUILD / "models-README.txt", models / "README.txt")
    shutil.copy(ROOT / "LICENSE", stage / "LICENSE")
    shutil.copy(BUILD / "THIRD_PARTY_NOTICES.md", stage / "THIRD_PARTY_NOTICES.md")
    copy_licenses(stage, rid)

    if rid.startswith("win"):
        target = output / f"{name}.zip"
        target.unlink(missing_ok=True)
        with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as bundle:
            for path in sorted(stage.rglob("*")):
                if path.is_file():
                    bundle.write(path, Path(name) / path.relative_to(stage))
    else:
        target = output / f"{name}.tar.gz"
        with tarfile.open(target, "w:gz") as bundle:
            bundle.add(stage, arcname=name, filter=clean_tar_entry)

    print(f"Package: {target} ({target.stat().st_size / 1024**2:.1f} MiB)")


def load_manifest():
    return json.loads((BUILD / "third-party.json").read_text(encoding="utf-8"))


def shipped_packages():
    """Returns the NuGet packages that the Host lock file resolves, as name to version. Projects are the engine's own code."""
    lock = json.loads(HOST_LOCK.read_text(encoding="utf-8"))
    packages = {}
    for section in lock["dependencies"].values():
        for name, info in section.items():
            if info.get("type") in ("Direct", "Transitive"):
                packages[name] = info["resolved"]
    return packages


def notice_problems():
    """Compares the manifest with the lock file, the license files and THIRD_PARTY_NOTICES.md."""
    manifest = load_manifest()
    notices = (BUILD / "THIRD_PARTY_NOTICES.md").read_text(encoding="utf-8")
    problems = []
    packages = shipped_packages()
    for name in sorted(packages):
        if name not in manifest["nuget"] and name not in manifest["buildOnly"]:
            problems.append(f"The package {name} {packages[name]} ships with the engine, but build/third-party.json does not list it. Add it with its license files.")
    for name in sorted(set(manifest["nuget"]) - set(packages)):
        problems.append(f"build/third-party.json lists {name}, but the engine no longer uses it. Remove it.")
    files = [file for entry in manifest["nuget"].values() for file in entry["files"]] + list(manifest["llamaCpp"]["bundled"].values())
    problems += [f"The license file build/licenses/{file} does not exist." for file in files if not (LICENSES / file).is_file()]
    names = list(manifest["nuget"]) + list(manifest["llamaCpp"]["bundled"])
    problems += [f"THIRD_PARTY_NOTICES.md does not name {name}." for name in names if name not in notices]
    return problems


def check_notices(args):
    problems = notice_problems()
    if problems:
        sys.exit("\n".join(problems))
    print("build/third-party.json lists every shipped package, and every license file exists.")


def copy_licenses(stage, rid):
    """Copies the full license and notice texts into the package. It stops the build when a text is missing."""
    problems = notice_problems()
    if problems:
        sys.exit("\n".join(problems))
    manifest = load_manifest()
    target = stage / "licenses"

    llama_license = stage / "llama" / manifest["llamaCpp"]["archiveFile"]
    if not llama_license.is_file():
        sys.exit(f"The llama.cpp archive has no {manifest['llamaCpp']['archiveFile']}, so the package would ship llama.cpp without its license.")
    (target / "llama.cpp").mkdir(parents=True)
    shutil.copy(llama_license, target / "llama.cpp" / "LICENSE")
    for file in manifest["llamaCpp"]["bundled"].values():
        shutil.copy(LICENSES / file, target / file)

    # The restore output names the exact packages of the build, and the folder that holds them.
    assets = json.loads(HOST_ASSETS.read_text(encoding="utf-8"))
    package_root = Path(assets["project"]["restore"]["packagesPath"])
    packages = shipped_packages()
    for name, entry in manifest["nuget"].items():
        folder = target / "nuget" / name
        folder.mkdir(parents=True)
        for file in entry["files"]:
            shutil.copy(LICENSES / file, folder / Path(file).name)
        for file in entry.get("packageFiles", []):
            source = package_root / name.lower() / packages[name] / file
            if not source.is_file():
                sys.exit(f"The package {name} {packages[name]} has no {file}.")
            shutil.copy(source, folder / file)

    downloads = {item["name"]: item["version"].strip("[]").split(",")[0].strip() for framework in assets["project"]["frameworks"].values() for item in framework.get("downloadDependencies", [])}
    for pack, files in manifest["runtimePacks"].items():
        name = f"{pack}.{rid}"
        if name not in downloads:
            sys.exit(f"The restore output has no {name}.")
        folder = target / "dotnet" / pack
        folder.mkdir(parents=True)
        for file in files:
            source = package_root / name.lower() / downloads[name] / file
            if not source.is_file():
                sys.exit(f"The runtime pack {name} {downloads[name]} has no {file}.")
            shutil.copy(source, folder / file)


def expected_license_files():
    """Gives the license files that every package must contain, relative to its root folder."""
    manifest = load_manifest()
    files = ["LICENSE", "THIRD_PARTY_NOTICES.md", "llama/LICENSE", "licenses/llama.cpp/LICENSE"]
    files += [f"licenses/{file}" for file in manifest["llamaCpp"]["bundled"].values()]
    for name, entry in manifest["nuget"].items():
        files += [f"licenses/nuget/{name}/{Path(file).name}" for file in entry["files"] + entry.get("packageFiles", [])]
    for pack, pack_files in manifest["runtimePacks"].items():
        files += [f"licenses/dotnet/{pack}/{file}" for file in pack_files]
    return files


def fetch_model(args):
    lock = load_lock("model.lock.json")
    url = f"https://huggingface.co/{lock['repository']}/resolve/{lock['revision']}/{lock['file']}"
    path = download_verified(url, Path(args.output) / lock["file"], lock["sha256"])
    print(f"Model: {path}")


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def http_json(url, body=None, timeout=900):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def process_exists_with_server_name(rid):
    """Tells whether a llama-server process is running, so the test can prove that shutdown stops it."""
    if rid.startswith("win"):
        result = subprocess.run(["tasklist", "/FI", "IMAGENAME eq llama-server.exe"], capture_output=True, text=True, check=False)
        return "llama-server.exe" in result.stdout
    return subprocess.run(["pgrep", "-x", "llama-server"], capture_output=True, check=False).returncode == 0


def smoke_test(args):
    rid = args.rid
    prompt = [{"role": "user", "content": "Reply with the single word: hello"}]
    model = Path(args.model).resolve()
    # Next to the model, because a hard link cannot cross disks. On Windows runners the temp folder is on another disk.
    with work_folder(model.parent) as work:
        extract(args.package, work)
        roots = [path for path in Path(work).iterdir() if path.is_dir()]
        if len(roots) != 1:
            sys.exit(f"Expected one top-level folder in the package, found {len(roots)}")
        root = roots[0]
        program = root / program_file_name(rid)
        if not (root / "llama" / server_file_name(rid)).exists():
            sys.exit("The package has no llama-server.")
        missing = [file for file in expected_license_files() if not (root / file).is_file()]
        if missing:
            sys.exit(f"The package has no license file {', '.join(missing)}.")
        # A hard link avoids copying 2 GiB.
        os.link(model, root / "models" / model.name)

        port = free_port()
        base = f"http://127.0.0.1:{port}"
        environment = dict(os.environ, Urls=base)
        if args.gpu_layers:
            environment["LocalModel__GpuLayers"] = args.gpu_layers
        # The working folder is deliberately different, to prove that the engine finds its files from its own location.
        engine = subprocess.Popen([str(program)], env=environment, cwd=tempfile.gettempdir())
        try:
            deadline = time.monotonic() + args.load_timeout
            while True:
                if engine.poll() is not None:
                    sys.exit(f"The engine exited early with code {engine.returncode}.")
                try:
                    health = http_json(f"{base}/health", timeout=5)
                    print(f"Health: {health}", flush=True)
                    if health["local"] == "ready":
                        break
                    if health["local"] == "failed":
                        sys.exit("The local model failed. See the engine output above.")
                    if health["local"] == "missing":
                        sys.exit("The engine could not find the model or llama-server.")
                except (urllib.error.URLError, ConnectionError, TimeoutError):
                    pass
                if time.monotonic() > deadline:
                    sys.exit("The model did not load in time.")
                time.sleep(2)

            # The ready route must agree with /health.
            ready = urllib.request.urlopen(f"{base}/health/ready?provider=local", timeout=5)
            if ready.status != 200:
                sys.exit(f"/health/ready?provider=local returned {ready.status}.")

            models = http_json(f"{base}/v1/models")
            ids = [item["id"] for item in models["data"]]
            print(f"Models: {ids}", flush=True)
            if "local/ministral-3-3b" not in ids:
                sys.exit("The local model is not listed.")

            answer = http_json(f"{base}/v1/chat/completions", {"model": "local/ministral-3-3b", "max_tokens": 16, "messages": prompt})
            text = answer["choices"][0]["message"]["content"]
            print(f"Complete answer: {text!r}, usage: {answer.get('usage')}", flush=True)
            if not text.strip():
                sys.exit("The complete answer was empty.")

            request = urllib.request.Request(
                f"{base}/v1/chat/completions",
                data=json.dumps({"model": "local/ministral-3-3b", "max_tokens": 16, "stream": True, "stream_options": {"include_usage": True}, "messages": prompt}).encode(),
                headers={"Content-Type": "application/json"},
            )
            pieces, done, usage = [], False, None
            with urllib.request.urlopen(request, timeout=900) as response:
                for raw in response:
                    line = raw.decode().strip()
                    if not line.startswith("data: "):
                        continue
                    payload = line[len("data: "):]
                    if payload == "[DONE]":
                        done = True
                        break
                    event = json.loads(payload)
                    if "error" in event:
                        sys.exit(f"The stream failed: {event['error']}")
                    usage = event.get("usage") or usage
                    for choice in event.get("choices", []):
                        pieces.append(choice["delta"].get("content") or "")
            print(f"Streamed answer: {''.join(pieces)!r}, pieces: {len(pieces)}, usage: {usage}", flush=True)
            if not done or not "".join(pieces).strip():
                sys.exit("The stream was incomplete.")
        finally:
            stop_engine(engine, rid)
            try:
                engine.wait(timeout=30)
            except subprocess.TimeoutExpired:
                engine.kill()
                sys.exit("The engine did not stop in time.")

        # On Windows the test stops the whole tree itself, so this check would prove nothing there.
        if not rid.startswith("win"):
            deadline = time.monotonic() + 15
            while process_exists_with_server_name(rid):
                if time.monotonic() > deadline:
                    sys.exit("llama-server continued to run after the engine stopped.")
                time.sleep(0.5)
    print("Smoke test passed.")


def finalize(args):
    folder = Path(args.folder)
    files = sorted(path for path in folder.iterdir() if path.is_file() and path.name != "SHA256SUMS")
    if not files:
        sys.exit(f"No files in {folder}")
    lines = []
    for path in files:
        size = path.stat().st_size
        print(f"{path.name}: {size} bytes")
        if size >= GITHUB_ASSET_LIMIT:
            sys.exit(f"{path.name} is {size} bytes, which GitHub would reject. The limit is {GITHUB_ASSET_LIMIT}.")
        lines.append(f"{sha256_of(path)}  {path.name}")
    (folder / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("All files are under the limit. The script wrote SHA256SUMS.")


CHANGELOG_HEADING = re.compile(r"^## \[(?P<label>[^\]]+)\](?: - (?P<date>\d{4}-\d{2}-\d{2}))?\s*$")


def changelog_sections(text):
    """Returns each '## [label]' section of a Keep a Changelog file as (label, date, body), in file order."""
    sections = []
    current = None
    for line in text.splitlines():
        match = CHANGELOG_HEADING.match(line)
        if match:
            current = [match["label"], match["date"], []]
            sections.append(current)
        elif line.startswith("## "):
            sys.exit(f"CHANGELOG.md has a heading that is not '## [version] - YYYY-MM-DD' or '## [Unreleased]': {line!r}")
        elif current is not None:
            current[2].append(line)
    return [(label, date, "\n".join(body).strip()) for label, date, body in sections]


def check_changelog(args):
    if not CHANGELOG.exists():
        sys.exit("CHANGELOG.md does not exist.")
    sections = changelog_sections(CHANGELOG.read_text(encoding="utf-8"))
    labels = [label for label, _, _ in sections]
    if not sections or labels[0] != "Unreleased":
        sys.exit("The first section of CHANGELOG.md must be '## [Unreleased]'.")
    duplicates = {label for label in labels if labels.count(label) > 1}
    if duplicates:
        sys.exit(f"CHANGELOG.md has more than one section for: {', '.join(sorted(duplicates))}")
    for label, date, _ in sections[1:]:
        if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?", label) or date is None:
            sys.exit(f"The CHANGELOG.md section '{label}' must be a version with a date, for example '## [1.2.3] - 2026-01-31'.")

    wanted = args.version or "Unreleased"
    section = next((entry for entry in sections if entry[0] == wanted), None)
    if section is None:
        sys.exit(f"CHANGELOG.md has no section for {wanted}. Add '## [{wanted}] - YYYY-MM-DD' before you push the tag.")
    if args.version and not section[2]:
        sys.exit(f"The CHANGELOG.md section for {wanted} is empty.")

    if args.notes:
        Path(args.notes).write_text((section[2] or "No changes are recorded.") + "\n", encoding="utf-8")
    print(f"CHANGELOG.md has a valid section for {wanted}.")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(required=True)

    command = commands.add_parser("package")
    command.add_argument("--rid", required=True, choices=load_lock("llama-cpp.lock.json")["assets"].keys())
    command.add_argument("--version", required=True, type=version_argument)
    command.add_argument("--output", default=str(ROOT / "artifacts"))
    command.set_defaults(run=package)

    command = commands.add_parser("fetch-model")
    command.add_argument("--output", default=str(ROOT / "artifacts" / "cache"))
    command.set_defaults(run=fetch_model)

    command = commands.add_parser("smoke-test")
    command.add_argument("--rid", required=True)
    command.add_argument("--package", required=True)
    command.add_argument("--model", required=True)
    command.add_argument("--gpu-layers", help="Overrides the GPU layer setting, for runners with an unusable GPU.")
    command.add_argument("--load-timeout", type=int, default=600)
    command.set_defaults(run=smoke_test)

    command = commands.add_parser("check-changelog")
    command.add_argument("--version", type=version_argument, help="The release version. Without it, the command checks the Unreleased section.")
    command.add_argument("--notes", help="Writes the body of the section to this file, for the release notes.")
    command.set_defaults(run=check_changelog)

    command = commands.add_parser("check-notices")
    command.set_defaults(run=check_notices)

    command = commands.add_parser("finalize")
    command.add_argument("--folder", required=True)
    command.set_defaults(run=finalize)

    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
