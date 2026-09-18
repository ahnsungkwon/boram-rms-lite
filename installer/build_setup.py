"""Build one first-install EXE from the existing verified Lite release. Developer only."""
from pathlib import Path
import hashlib, json, shutil, subprocess, zipfile
import xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[1]
VERSION = ET.parse(ROOT / 'BoramRms.Lite.csproj').findtext('./PropertyGroup/Version')
if ET.parse(ROOT / 'installer/Setup.csproj').findtext('./PropertyGroup/AppVersion') != VERSION:
    raise RuntimeError('Installer payload version differs from the app project')
OUT = ROOT / 'dist' / 'setup' / VERSION
BUNDLE = ROOT / 'dist' / 'releases' / VERSION

def digest(path):
    h=hashlib.sha256()
    with path.open('rb') as f:
        for b in iter(lambda:f.read(1048576),b''):h.update(b)
    return h.hexdigest()

def run(args):
    print('RUN:', ' '.join(map(str,args)),flush=True)
    subprocess.run(list(map(str,args)),cwd=ROOT,check=True)

def main():
    meta=json.loads((BUNDLE/'update-manifest.json').read_text('utf-8'))
    package=BUNDLE/meta['Package']
    if meta['Version']!=VERSION or digest(package)!=meta['Sha256'] or package.stat().st_size!=meta['Size']:raise RuntimeError('Release archive mismatch')
    with zipfile.ZipFile(package) as z:
        if z.testzip():raise RuntimeError('Release CRC failure')
        if any(Path(n).suffix.lower() in ['.ttf','.otf','.woff','.woff2','.dpapi','.key','.pem','.pfx'] for n in z.namelist()):raise RuntimeError('Unexpected font or credential in package')
    run(['dotnet','build','installer/Setup.csproj','-c','Release','--nologo'])
    built=ROOT/'installer/bin/Release/net48/BoramRMS_Lite_Setup.exe'
    OUT.mkdir(parents=True,exist_ok=True)
    target=OUT/f'BoramRMS_Lite_{VERSION}_Setup.exe'
    if target.exists():raise RuntimeError('Setup output already exists; do not overwrite')
    proof=ROOT/'tests-data'/('setup-'+VERSION.replace('.',''))
    run([built,'--self-test',proof])
    actual=Path((proof/'latest-run.txt').read_text('utf-8').strip())
    result=json.loads((actual/'RESULT.json').read_text('utf-8'))
    if result['failed'] or result['passed']<15:raise RuntimeError('Setup tests failed; no final installer was created')
    font_proof=ROOT/'tests-data'/('setup-font-'+VERSION.replace('.',''))
    run([built,'--font-probe',font_proof])
    font_result=json.loads((font_proof/'FONT_PROBE.json').read_text('utf-8'))
    if font_result.get('success') is not True or font_result.get('registryChanged') is not False:
        raise RuntimeError('Optional font download/memory probe did not pass')
    shutil.copy2(built,target)
    if digest(built)!=digest(target):raise RuntimeError('Installer copy verification failed')
    shutil.copy2(ROOT/'installer/FIRST_INSTALL.html',OUT/'FIRST_INSTALL.html')
    shutil.copy2(actual/'SUMMARY.txt',OUT/'SETUP_TEST_SUMMARY.txt')
    shutil.copy2(actual/'setup-preview.png',OUT/'SETUP_PREVIEW.png')
    data={'version':VERSION,'setup':str(target),'size':target.stat().st_size,'sha256':digest(target),'tests':result['passed'],'appTests':result['appTests'],'fontProbe':str(font_proof/'FONT_PROBE.json'),'fontProbePassed':True,'failures':0,'proof':str(actual),'fontsBundled':False,'pythonRequiredOnTarget':False,'unsigned':True}
    with (OUT/'SETUP_BUILD_RESULT.json').open('x',encoding='utf-8') as f:json.dump(data,f,ensure_ascii=False,indent=2)
    print(json.dumps(data,ensure_ascii=False,indent=2),flush=True)
if __name__=='__main__':main()
