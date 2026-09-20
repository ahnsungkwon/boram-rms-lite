"""Build/test/package and explicitly publish only Boram RMS Lite Releases."""
from __future__ import annotations
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = 'ahnsungkwon/boram-rms-lite'
VERSION = ET.parse(ROOT / 'BoramRms.Lite.csproj').findtext('./PropertyGroup/Version')
if not VERSION or not re.fullmatch(r'\d+\.\d+\.\d+', VERSION):
    raise SystemExit('Invalid stable project version.')
# Public packages use a fresh tree; pre-merge verified artifacts stay untouched.
OUT = ROOT / 'dist' / 'public' / 'retry-1' / 'releases' / VERSION
BUNDLE = OUT / 'BoramRMS_Lite'
ZIP = OUT / f'BoramRMS_Lite_{VERSION}_win-x64.zip'
META = OUT / 'update-manifest.json'
PROOF = ROOT / 'tests-data' / 'public' / 'retry-1' / 'releases' / VERSION


def run(args: list[str], *, capture: bool = False) -> str:
    print('RUN:', ' '.join(args), flush=True)
    result = subprocess.run(args, cwd=ROOT, check=True, text=True, encoding='utf-8', errors='replace', stdout=subprocess.PIPE if capture else None, stderr=subprocess.PIPE if capture else None)
    return result.stdout or ''


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open('rb') as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


def write_new(path: Path, value: object) -> None:
    with path.open('x', encoding='utf-8') as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)


LICENSE_NAMES = ('license.txt', 'license')
NOTICE_NAMES = ('third-party-notices.txt', 'thirdpartynotices.txt', 'third-party-notices')
DESKTOP_PACKAGE = 'microsoft.windowsdesktop.app.runtime.win-x64'
DESKTOP_VERSION = '8.0.31'
DESKTOP_NUSPEC_SHA = '8b46f6be443fa437fa52d431dbb4b34b9834d0cab3a663a92a19ce57f3008d96'
DESKTOP_LICENSE_SHA = 'a89886665765362eb77e0f8e26602c924520041d1711b2eedc136434fe4d01ab'


def select_runtime_notices(source: Path, package: str, version: str):
    available = {p.name.casefold(): p for p in source.iterdir() if p.is_file()}
    license_file = next((available[n] for n in LICENSE_NAMES if n in available), None)
    notice_files = [available[n] for n in NOTICE_NAMES if n in available]
    if license_file is None:
        raise SystemExit('Runtime license missing: ' + package)
    if not notice_files:
        nuspec = source / (package + '.nuspec')
        if not (package == DESKTOP_PACKAGE and version == DESKTOP_VERSION
                and nuspec.is_file() and digest(nuspec) == DESKTOP_NUSPEC_SHA
                and digest(license_file) == DESKTOP_LICENSE_SHA):
            raise SystemExit('Unreviewed runtime notice omission: ' + package)
        hidden_notices = [p for p in source.rglob('*') if p.is_file()
                          and ('notice' in p.name.casefold() or 'thirdparty' in p.name.casefold())]
        if hidden_notices:
            raise SystemExit('Additional runtime notices require review')
    return [license_file, *notice_files]


def validate_runtime_notice_inventory(notices):
    expected = {'microsoft.netcore.app.runtime.win-x64', DESKTOP_PACKAGE}
    if len(notices) != 2 or {item.get('package') for item in notices} != expected:
        raise SystemExit('Runtime notice inventory incomplete')
    for item in notices:
        package, version, files = item['package'], item['version'], item['files']
        if not re.fullmatch(r'\d+\.\d+\.\d+', version):
            raise SystemExit('Invalid runtime notice version')
        names = [Path(entry['name']).name.casefold() for entry in files]
        if len(names) != len(set(names)) or sum(n in LICENSE_NAMES for n in names) != 1:
            raise SystemExit('Missing or duplicate runtime license')
        if any(n not in LICENSE_NAMES + NOTICE_NAMES for n in names):
            raise SystemExit('Unexpected runtime notice file')
        if not any(n in NOTICE_NAMES for n in names):
            if not (package == DESKTOP_PACKAGE and version == DESKTOP_VERSION and len(files) == 1
                    and files[0]['sha256'] == DESKTOP_LICENSE_SHA
                    and item.get('reviewedNuspecSha256') == DESKTOP_NUSPEC_SHA
                    and item.get('separateNoticeProvided') is False):
                raise SystemExit('Unreviewed runtime notice omission')
        for entry in files:
            prefix = 'licenses/' + package + '-' + version + '/'
            if entry['name'] != prefix + Path(entry['name']).name:
                raise SystemExit('Unexpected runtime notice path')


def copy_runtime_notices() -> None:
    """Keep the notices shipped with the exact restored runtime packages."""
    assets = json.loads((ROOT / 'obj' / 'project.assets.json').read_text('utf-8'))
    config = json.loads((BUNDLE / 'BoramRms.Lite.runtimeconfig.json').read_text('utf-8'))
    frameworks = config['runtimeOptions']['includedFrameworks']
    records = []
    for framework in frameworks:
        name, version = framework['name'], framework['version']
        if name not in {'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App'}:
            raise SystemExit('Unreviewed runtime: ' + name)
        if not re.fullmatch(r'\d+\.\d+\.\d+', version):
            raise SystemExit('Unexpected runtime version')
        package = (name + '.Runtime.win-x64').lower()
        candidates = [Path(folder) / package / version for folder in assets['packageFolders']]
        source = next((folder for folder in candidates if folder.is_dir()), None)
        if source is None:
            raise SystemExit('Restored runtime package not found: ' + package)
        chosen = select_runtime_notices(source, package, version)
        destination = BUNDLE / 'licenses' / (package + '-' + version)
        destination.mkdir(parents=True, exist_ok=False)
        files = []
        for source_file in chosen:
            raw = source_file.read_bytes()
            if not 100 <= len(raw) <= 5_000_000 or '\x00' in raw.decode('utf-8-sig'):
                raise SystemExit('Invalid runtime notice text: ' + source_file.name)
            target = destination / source_file.name
            with target.open('xb') as handle:
                handle.write(raw)
            files.append({'name': target.relative_to(BUNDLE).as_posix(), 'sha256': digest(target)})
        record = {'package': package, 'version': version, 'files': files,
                  'separateNoticeProvided': any(p.name.casefold() in NOTICE_NAMES for p in chosen)}
        if not record['separateNoticeProvided']:
            record['reviewedNuspecSha256'] = digest(source / (package + '.nuspec'))
        records.append(record)
    validate_runtime_notice_inventory(records)
    write_new(BUNDLE / 'licenses' / 'RUNTIME_NOTICES.json', records)


def build() -> None:
    if OUT.exists():
        raise SystemExit(f'Release output exists. Do not overwrite: {OUT}')
    OUT.mkdir(parents=True)
    run(['dotnet', 'publish', 'BoramRms.Lite.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', str(BUNDLE), '--nologo'])
    run([str(BUNDLE / 'BoramRms.Lite.exe'), '--self-test', str(PROOF)])
    proof = Path((PROOF / 'latest-run.txt').read_text(encoding='utf-8').strip()).resolve()
    if not proof.is_relative_to(PROOF.resolve()):
        raise SystemExit('Test output escaped the expected directory.')
    summary = (proof / 'SUMMARY.txt').read_text(encoding='utf-8')
    results = json.loads((proof / 'test-results.json').read_text(encoding='utf-8'))
    # 0.4 replaces legacy cross-folder/journal tests with the actual simple workflow.
    required_workflow = {f'W{n:02d}' for n in range(1, 21)} | {f'A{n:02d}' for n in range(1, 25)} | {f'B{n:02d}' for n in range(1, 13)} | {f'T{n:02d}' for n in range(1, 17)} | {f'D{n:02d}' for n in range(1, 19)} | {f'K{n:02d}' for n in range(1, 9)} | {f'L{n:02d}' for n in range(1, 15)}
    covered_workflow = {item.get('Name', '').split(' ', 1)[0] for item in results}
    required_workflow |= {f'PF{n:02d}' for n in range(1, 9)} | {f'NC{n:02d}' for n in range(1, 8)} | {f'SP{n:02d}' for n in range(1, 5)}
    required_workflow |= {f'AR{n:02d}' for n in range(1, 9)}
    if len(results) < 178 or not required_workflow.issubset(covered_workflow) or not all(item.get('Passed') is True for item in results) or not summary.startswith(f'PASS {len(results)}\nFAIL 0'):
        raise SystemExit('Test failure: package will not be created.')
    for name in ['README.md', 'README_KO.md', 'UPDATE_GUIDE.md', 'CHANGELOG.md', 'SIMPLE_WORKFLOW.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'PUBLIC_SHARING.md']:
        shutil.copy2(ROOT / name, BUNDLE / name)
    for name, target in [('SUMMARY.txt', 'TEST_SUMMARY.txt'), ('main-preview.png', 'MAIN_PREVIEW.png'), ('update-preview.png', 'UPDATE_PREVIEW.png'), ('rename-input-preview.png', 'RENAME_INPUT_PREVIEW.png'), ('rms-icon-preview.png', 'RMS_ICON_PREVIEW.png'), ('autosave-preview.png', 'AUTOSAVE_PREVIEW.png'), ('blank-name-preview.png', 'BLANK_NAME_PREVIEW.png')] + [(f'theme-{name}.png', f'THEME_{name.upper()}.png') for name in ['green', 'blue', 'purple', 'pink']]:
        shutil.copy2(proof / name, BUNDLE / target)
    for name in ['compact-main', 'compact-1024', 'guide-start', 'guide-shortcuts', 'folder-picker', 'dock-before', 'dock-expanded']:
        shutil.copy2(proof / (name + '.png'), BUNDLE / (name.upper() + '.png'))
    copy_runtime_notices()
    entries = sorted(p for p in BUNDLE.rglob('*') if p.is_file())
    forbidden = {'.ttf', '.otf', '.woff', '.woff2', '.pfx', '.pem', '.key', '.dpapi'}
    if any(p.is_symlink() or p.suffix.lower() in forbidden or p.name.startswith('.env') for p in entries):
        raise SystemExit('Unexpected credential, link, or font in bundle.')
    files = {p.relative_to(BUNDLE).as_posix(): {'Size': p.stat().st_size, 'Sha256': digest(p)} for p in entries}
    marker = BUNDLE / '.lite-install.json'
    write_new(marker, {'Product': 'BoramRms.Lite', 'Repository': REPO, 'Version': VERSION, 'Files': files})
    with zipfile.ZipFile(ZIP, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in entries + [marker]:
            archive.write(path, 'BoramRMS_Lite/' + path.relative_to(BUNDLE).as_posix())
    with zipfile.ZipFile(ZIP) as archive:
        if archive.testzip():
            raise SystemExit('ZIP CRC verification failed.')
    metadata = {'Product': 'BoramRms.Lite', 'Repository': REPO, 'Version': VERSION, 'Package': ZIP.name, 'Size': ZIP.stat().st_size, 'Sha256': digest(ZIP), 'Root': 'BoramRMS_Lite'}
    write_new(META, metadata)
    write_new(OUT / 'BUILD_RESULT.json', {'version': VERSION, 'tests': len(results), 'failures': 0, 'proof': str(proof), 'archive': str(ZIP), 'sha256': metadata['Sha256'], 'fileCount': len(entries) + 1, 'zipCrc': 'passed'})
    print(json.dumps(metadata, ensure_ascii=False, indent=2), flush=True)


def verify_local() -> dict:
    metadata = json.loads(META.read_text(encoding='utf-8'))
    if metadata['Version'] != VERSION or metadata['Repository'] != REPO or digest(ZIP) != metadata['Sha256'] or ZIP.stat().st_size != metadata['Size']:
        raise SystemExit('Release package identity/hash does not match.')
    with zipfile.ZipFile(ZIP) as archive:
        for name in ['LICENSE', 'THIRD_PARTY_NOTICES.md', 'PUBLIC_SHARING.md', 'README.md', 'README_KO.md', 'UPDATE_GUIDE.md', 'CHANGELOG.md']:
            if archive.read('BoramRMS_Lite/' + name) != (ROOT / name).read_bytes():
                raise SystemExit('Packaged public document differs from source: ' + name)
        notices = json.loads(archive.read('BoramRMS_Lite/licenses/RUNTIME_NOTICES.json'))
        validate_runtime_notice_inventory(notices)
        for item in notices:
            for entry in item['files']:
                if hashlib.sha256(archive.read('BoramRMS_Lite/' + entry['name'])).hexdigest() != entry['sha256']:
                    raise SystemExit('Runtime notice hash mismatch')
    return metadata


def publish() -> None:
    metadata = verify_local()
    repository = json.loads(run(['gh', 'repo', 'view', REPO, '--json', 'nameWithOwner,isPrivate,url'], capture=True))
    if repository['nameWithOwner'] != REPO or repository['isPrivate'] is not False:
        raise SystemExit('Only the expected public repository may be published.')
    if run(['git', 'status', '--porcelain'], capture=True).strip():
        raise SystemExit('Commit and push reviewed source changes before publishing.')
    if run(['git', 'branch', '--show-current'], capture=True).strip() != 'main':
        raise SystemExit('Publish from main only.')
    commit = run(['git', 'rev-parse', 'HEAD'], capture=True).strip()
    remote_commit = json.loads(run(['gh', 'api', f'repos/{REPO}/commits/main'], capture=True))['sha']
    if commit != remote_commit:
        raise SystemExit('Remote main and the local source commit differ.')
    tag = 'v' + VERSION
    existing = json.loads(run(['gh', 'api', f'repos/{REPO}/releases?per_page=100'], capture=True))
    if any(item['tag_name'] == tag for item in existing):
        raise SystemExit('This release already exists. Refusing overwrite.')
    run(['gh', 'release', 'create', tag, str(ZIP), str(META), '--repo', REPO, '--target', commit, '--draft', '--title', f'Boram RMS Lite {VERSION}', '--notes-file', str(ROOT / 'CHANGELOG.md')])
    # The tag endpoint returns published releases only; inspect the draft via list.
    drafts = json.loads(run(['gh', 'api', f'repos/{REPO}/releases?per_page=100'], capture=True))
    matching = [item for item in drafts if item['tag_name'] == tag and item['draft']]
    if len(matching) != 1 or matching[0]['target_commitish'] != commit:
        raise SystemExit('Could not identify the exact newly-created draft. No publication performed.')
    data = matching[0]
    assets = {item['name']: item for item in data['assets']}
    for path in [ZIP, META]:
        item = assets[path.name]
        if item['size'] != path.stat().st_size or item.get('digest') != 'sha256:' + digest(path):
            raise SystemExit('GitHub asset digest mismatch. Release kept as draft.')
    run(['gh', 'release', 'edit', tag, '--repo', REPO, '--draft=false', '--latest'])
    data = json.loads(run(['gh', 'api', f'repos/{REPO}/releases/latest'], capture=True))
    if data['tag_name'] != tag or data['draft'] or data['prerelease']:
        raise SystemExit('Published release verification failed.')
    write_new(OUT / 'PUBLISH_RESULT.json', {'repository': repository['url'], 'private': repository['isPrivate'], 'commit': commit, 'tag': tag, 'release': data['html_url'], 'published': data['published_at'], 'sha256': metadata['Sha256'], 'assetDigestsVerified': True})
    print(data['html_url'], flush=True)


def install_initial() -> None:
    verify_local()
    stable = ROOT / 'dist' / 'BoramRMS_Lite'
    if stable.exists():
        raise SystemExit('Stable app folder already exists; use the in-app updater. No overwrite performed.')
    shutil.copytree(BUNDLE, stable)
    print('Initial independent app:', stable / 'BoramRms.Lite.exe', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['build', 'publish', 'install-initial'])
    args = parser.parse_args()
    try:
        {'build': build, 'publish': publish, 'install-initial': install_initial}[args.action]()
    except subprocess.CalledProcessError as error:
        raise SystemExit(f'Command failed with exit code {error.returncode}. No existing release was overwritten.')
