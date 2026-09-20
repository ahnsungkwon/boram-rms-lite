"""Verify public release downloads without GitHub credentials; never install them."""
from __future__ import annotations
import hashlib
import json
import urllib.parse
import urllib.request
import uuid
import zipfile
from pathlib import PurePosixPath
import release

ALLOWED_HOSTS = {
    'api.github.com', 'github.com', 'release-assets.githubusercontent.com',
    'objects.githubusercontent.com', 'objects-origin.githubusercontent.com',
}
MAX_JSON = 2_000_000
MAX_ASSET = 350_000_000


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def safe_url(url: str) -> str:
    parsed = urllib.parse.urlsplit(url)
    require(parsed.scheme == 'https' and parsed.hostname in ALLOWED_HOSTS
            and parsed.port in (None, 443) and parsed.username is None
            and parsed.password is None, 'Unexpected download host or credentials')
    return url


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return super().redirect_request(req, fp, code, msg, headers, safe_url(newurl))


# No cookie jar, .netrc reader, bearer token, or GitHub CLI invocation is used.
OPENER = urllib.request.build_opener(SafeRedirect())


def request(url: str, accept: str = 'application/vnd.github+json'):
    req = urllib.request.Request(safe_url(url), headers={
        'User-Agent': 'BoramRMS-Lite-Public-Verification/' + release.VERSION,
        'Accept': accept,
    })
    response = OPENER.open(req, timeout=120)
    safe_url(response.geturl())
    return response


def get_json(url: str) -> dict:
    with request(url) as response:
        data = response.read(MAX_JSON + 1)
    require(len(data) <= MAX_JSON, 'JSON response exceeds budget')
    result = json.loads(data)
    require(isinstance(result, dict), 'Expected JSON object')
    return result


def main() -> None:
    local_manifest = release.verify_local()
    setup_dir = release.ROOT / 'dist' / 'public' / 'setup' / release.VERSION
    setup_build = json.loads((setup_dir / 'SETUP_BUILD_RESULT.json').read_text('utf-8'))
    require(setup_build['failures'] == 0 and setup_build['tests'] >= 15
            and setup_build['appTests'] >= 151, 'Required installer tests are missing')
    api = 'https://api.github.com/repos/' + release.REPO
    repository = get_json(api)
    require(repository.get('full_name') == release.REPO and repository.get('private') is False,
            'Anonymous access did not confirm the public repository')
    published = get_json(api + '/releases/latest')
    tag = 'v' + release.VERSION
    require(published.get('tag_name') == tag and published.get('draft') is False
            and published.get('prerelease') is False, 'Expected latest stable release is absent')
    local_publication = json.loads((release.OUT / 'PUBLISH_RESULT.json').read_text('utf-8'))
    require(published.get('target_commitish') == local_publication['commit'],
            'Release source commit differs from the published proof')
    assets = {item['name']: item for item in published['assets']}
    paths = [release.ZIP, release.META,
             setup_dir / f'BoramRMS_Lite_{release.VERSION}_Setup.exe',
             setup_dir / 'FIRST_INSTALL.html', setup_dir / 'SETUP_INFO.json']
    run = release.ROOT / 'tests-data' / 'public' / 'anonymous-downloads' / (release.VERSION + '-' + uuid.uuid4().hex)
    run.mkdir(parents=True, exist_ok=False)
    checks = []
    for local in paths:
        asset = assets.get(local.name)
        require(asset is not None, 'Missing public asset: ' + local.name)
        size = local.stat().st_size
        expected = release.digest(local)
        require(0 < size <= MAX_ASSET and asset.get('size') == size
                and asset.get('digest') == 'sha256:' + expected,
                'Asset metadata differs from the tested local file: ' + local.name)
        url = asset['browser_download_url']
        expected_prefix = 'https://github.com/' + release.REPO + '/releases/download/' + tag + '/'
        require(url.startswith(expected_prefix), 'Unexpected release download URL')
        target = run / local.name
        checksum = hashlib.sha256()
        count = 0
        with request(url, 'application/octet-stream') as response, target.open('xb') as output:
            while block := response.read(1_048_576):
                count += len(block)
                require(count <= size, 'Download exceeds expected size')
                output.write(block)
                checksum.update(block)
        require(count == size and checksum.hexdigest() == expected,
                'Anonymous download hash mismatch: ' + local.name)
        checks.append({'name': local.name, 'bytes': count, 'sha256': expected, 'anonymous': True})
        print('VERIFIED anonymous download:', local.name, count, flush=True)
    require(json.loads((run / release.META.name).read_text('utf-8')) == local_manifest,
            'Downloaded update manifest differs')
    archive_path = run / release.ZIP.name
    with zipfile.ZipFile(archive_path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), 'Duplicate ZIP entry')
        marker_name = 'BoramRMS_Lite/.lite-install.json'
        marker = json.loads(archive.read(marker_name))
        require(marker['Product'] == 'BoramRms.Lite' and marker['Repository'] == release.REPO
                and marker['Version'] == release.VERSION, 'Installation identity mismatch')
        files = marker['Files']
        expected_names = {'BoramRMS_Lite/' + name for name in files} | {marker_name}
        require(set(names) == expected_names, 'ZIP file inventory mismatch')
        for name, metadata in files.items():
            relative = PurePosixPath(name)
            require(not relative.is_absolute() and '..' not in relative.parts and ':' not in name
                    and '\\' not in name, 'Unsafe ZIP path')
            info = archive.getinfo('BoramRMS_Lite/' + name)
            require(info.file_size == metadata['Size'], 'ZIP entry length mismatch')
            checksum = hashlib.sha256()
            with archive.open(info) as stream:
                while block := stream.read(1_048_576):
                    checksum.update(block)
            require(checksum.hexdigest().lower() == metadata['Sha256'].lower(), 'ZIP entry hash mismatch')
        for name in ['LICENSE', 'THIRD_PARTY_NOTICES.md', 'PUBLIC_SHARING.md']:
            require(archive.read('BoramRMS_Lite/' + name) == (release.ROOT / name).read_bytes(),
                    'Missing or stale public license document: ' + name)
    result = {'success': True, 'version': release.VERSION, 'repository': release.REPO,
              'private': False, 'release': published['html_url'], 'anonymous': True,
              'assets': checks, 'verifiedFiles': len(files) + 1,
              'licenseDocumentsVerified': True, 'installed': False,
              'credentialsChanged': False, 'downloadDirectory': str(run)}
    release.write_new(run / 'RESULT.json', result)
    proof = release.OUT / 'PUBLIC_DOWNLOAD_RESULT.json'
    if not proof.exists():
        release.write_new(proof, result)
    print(json.dumps(result, ensure_ascii=False, indent=2), flush=True)


if __name__ == '__main__':
    main()
