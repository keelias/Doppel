"""
Builds Doppel.dll and makes the zip Thunderstore takes (dist/Doppel-<version>.zip):

    python package.py

The game's folder, if it is not Steam's usual one:  set GameDir=D:\Games\BONELAB  first.
"""
import json
import os
import shutil
import subprocess
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))


def main():
    version = json.load(open(os.path.join(HERE, "thunderstore", "manifest.json"), encoding="utf-8"))["version_number"]
    command = ["dotnet", "build", os.path.join(HERE, "Mods", "Doppel", "Doppel.csproj"), "-c", "Release", "-nologo", "-v", "q"]
    if os.environ.get("GameDir"):
        command.append("-p:GameDir=" + os.environ["GameDir"])
    built = subprocess.run(command, capture_output=True, text=True)
    if built.returncode != 0:
        print(built.stdout[-3000:])
        raise SystemExit("the build failed")
    out = os.path.join(HERE, "dist", "Doppel")
    if os.path.isdir(out):
        shutil.rmtree(out)
    os.makedirs(os.path.join(out, "Mods"))
    shutil.copyfile(os.path.join(HERE, "Mods", "Doppel", "bin", "Release", "Doppel.dll"), os.path.join(out, "Mods", "Doppel.dll"))
    # What the mod comes with (its maker's recordings): the mod puts them in place the first time it runs.
    shutil.copytree(os.path.join(HERE, "starter"), os.path.join(out, "UserData", "Bot Player", "starter"))
    for name in ("manifest.json", "README.md", "CHANGELOG.md", "icon.png"):
        shutil.copyfile(os.path.join(HERE, "thunderstore", name), os.path.join(out, name))
    zipped = os.path.join(HERE, "dist", "Doppel-" + version + ".zip")
    if os.path.isfile(zipped):
        os.remove(zipped)
    with zipfile.ZipFile(zipped, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, files in os.walk(out):
            for f in files:
                full = os.path.join(root, f)
                z.write(full, os.path.relpath(full, out).replace(os.sep, "/"))
    print("made " + zipped)


if __name__ == "__main__":
    main()
