"""Append verified first-install assets to the existing public app release.
Explicit publishing operation: adds assets only, never replaces or retags a release.
"""
from __future__ import annotations
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPO = 'ahnsungkwon/boram-rms-lite'
VERSION = ET.parse(ROOT / 'BoramRms.Lite.csproj').findtext('./PropertyGroup/Version')
SETUP_VERSION = ET.parse(ROOT / 'installer/Setup.csproj').findtext('./PropertyGroup/Version')
TAG = 'v' + VERSION
OUT = ROOT / 'dist' / 'public' / 'retry-1' / 'setup' / VERSION
APP_RELEASE = ROOT / 'dist' / 'public' / 'retry-1' / 'releases' / VERSION
SOURCE_FILES = [
    'BoramRms.Lite.csproj', 'installer/Setup.csproj', 'installer/app.manifest',
    'installer/InstallCore.cs', 'installer/FontInstaller.cs', 'installer/Program.cs',
    'installer/SetupTests.cs', 'installer/build_setup.py', 'installer/FIRST_INSTALL.html',
    'installer/publish_setup.py', 'installer/README.md',
    'MainWindow.DockLayout.cs', 'DockLayoutTests.cs', 'MainWindow.xaml', 'MainWindow.xaml.cs',
    'MainWindow.Update.cs', 'GuideWindow.cs', 'LiteWorkflowTests.cs', 'release.py',
    'LiteFileIo.cs', 'LiteWorkspace.cs', 'FileLockTests.cs',
    'MainWindow.AutoStatus.cs', 'MainWindow.Simple.cs',
    'LICENSE', 'THIRD_PARTY_NOTICES.md', 'PUBLIC_SHARING.md',
]

def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1048576), b''):
            h.update(block)
    return h.hexdigest()

def run(args: list[str]) -> str:
    print('RUN:', ' '.join(args), flush=True)
    p = subprocess.run(args, cwd=ROOT, check=True, text=True, encoding='utf-8',
                       errors='replace', stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return p.stdout

def api(path: str) -> dict:
    return json.loads(run(['gh', 'api', path]))

def asset_ok(asset: dict, path: Path) -> bool:
    return asset.get('size') == path.stat().st_size and asset.get('digest') == 'sha256:' + digest(path)

def main() -> None:
    build = json.loads((OUT / 'SETUP_BUILD_RESULT.json').read_text('utf-8'))
    setup = OUT / f'BoramRMS_Lite_{VERSION}_Setup.exe'
    guide = OUT / 'FIRST_INSTALL.html'
    if (build['version'] != VERSION or build['tests'] < 15 or build['failures'] != 0
            or build.get('appTests', 0) < 151 or build.get('fontProbePassed') is not True
            or build['fontsBundled'] is not False or build['pythonRequiredOnTarget'] is not False
            or setup.stat().st_size != build['size'] or digest(setup) != build['sha256']):
        raise RuntimeError('Installer identity, tests, size or hash does not match.')
    if digest(guide) != digest(ROOT / 'installer/FIRST_INSTALL.html'):
        raise RuntimeError('Guide differs from the tested installer source.')
    if run(['git', 'status', '--porcelain']).strip():
        raise RuntimeError('Commit and push the reviewed installer source first.')
    if run(['git', 'branch', '--show-current']).strip() != 'main':
        raise RuntimeError('Publish from main only.')
    commit = run(['git', 'rev-parse', 'HEAD']).strip()
    repository = json.loads(run(['gh', 'repo', 'view', REPO, '--json', 'nameWithOwner,isPrivate,url']))
    if repository['nameWithOwner'] != REPO or repository['isPrivate'] is not False:
        raise RuntimeError('The intended public repository was not confirmed.')
    if api(f'repos/{REPO}/commits/main')['sha'] != commit:
        raise RuntimeError('Installer source is not on remote main.')
    original = json.loads((APP_RELEASE / 'PUBLISH_RESULT.json').read_text('utf-8'))
    release = api(f'repos/{REPO}/releases/tags/{TAG}')
    if (release['draft'] or release['prerelease'] or release['tag_name'] != TAG
            or release['target_commitish'] != original['commit']):
        raise RuntimeError('Existing app release identity differs; no upload performed.')
    assets = {a['name']: a for a in release['assets']}
    app_zip = APP_RELEASE / f'BoramRMS_Lite_{VERSION}_win-x64.zip'
    app_meta = APP_RELEASE / 'update-manifest.json'
    for path in [app_zip, app_meta]:
        if path.name not in assets or not asset_ok(assets[path.name], path):
            raise RuntimeError('Original app update asset does not match.')
    before = {a['id']: (a['name'], a['size'], a.get('digest')) for a in release['assets']}
    info = {
        'product': 'BoramRms.Lite.Setup', 'appVersion': VERSION, 'setupVersion': SETUP_VERSION,
        'file': setup.name, 'size': setup.stat().st_size, 'sha256': digest(setup),
        'installerTestsPassed': build['tests'], 'appTestsPassed': build['appTests'],
        'fontsBundled': False, 'pythonRequiredOnTarget': False, 'codeSigned': False,
        'scope': 'current Windows user', 'sourceCommit': commit,
        'sourceFiles': {name: digest(ROOT / name) for name in SOURCE_FILES},
        'fontDownload': 'optional; fetched directly from the official publisher on the target PC',
        'fontRegistrationTestedOnRealProfile': False,
    }
    probe_path = ROOT / 'tests-data' / 'public' / 'retry-1' / ('setup-font-' + VERSION.replace('.', '')) / 'FONT_PROBE.json'
    if probe_path.exists():
        probe = json.loads(probe_path.read_text('utf-8'))
        info['fontDownloadAndMemoryProbePassed'] = probe.get('success') is True
    else:
        info['fontDownloadAndMemoryProbePassed'] = None
    info_path = OUT / 'SETUP_INFO.json'
    if info_path.exists():
        if json.loads(info_path.read_text('utf-8')) != info:
            raise RuntimeError('Different setup metadata already exists; refusing overwrite.')
    else:
        with info_path.open('x', encoding='utf-8') as f:
            json.dump(info, f, ensure_ascii=False, indent=2)
    additions = [setup, guide, info_path]
    for path in additions:
        if path.name in assets and not asset_ok(assets[path.name], path):
            raise RuntimeError('An asset with this name differs: ' + path.name)
    missing = [str(p) for p in additions if p.name not in assets]
    if missing:
        run(['gh', 'release', 'upload', TAG, *missing, '--repo', REPO])
    after = api(f'repos/{REPO}/releases/tags/{TAG}')
    if after['id'] != release['id'] or after['target_commitish'] != release['target_commitish']:
        raise RuntimeError('Release identity changed during upload.')
    now = {a['id']: (a['name'], a['size'], a.get('digest')) for a in after['assets']}
    if any(now.get(key) != value for key, value in before.items()):
        raise RuntimeError('An original release asset changed.')
    verified = {a['name']: a for a in after['assets']}
    for path in additions:
        if path.name not in verified or not asset_ok(verified[path.name], path):
            raise RuntimeError('Uploaded installer asset failed verification: ' + path.name)
    result = {
        'success': True, 'repository': REPO, 'private': repository['isPrivate'], 'tag': TAG,
        'release': after['html_url'], 'installerSourceCommit': commit,
        'appReleaseCommitUnchanged': release['target_commitish'],
        'originalAssetsUnchanged': True, 'assetDigestsVerified': True,
        'assets': [{'name': p.name, 'size': p.stat().st_size, 'sha256': digest(p),
                    'url': verified[p.name]['browser_download_url']} for p in additions],
    }
    report = OUT / 'SETUP_PUBLISH_RESULT.json'
    if not report.exists():
        with report.open('x', encoding='utf-8') as f:
            json.dump(result, f, ensure_ascii=False, indent=2)
    print(json.dumps(result, ensure_ascii=False, indent=2), flush=True)

if __name__ == '__main__':
    main()
