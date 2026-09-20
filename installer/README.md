# 보람 RMS Lite — 처음 설치 도구 1.0.2

공개 저장소 · MIT License. 설치 파일 링크 공유에 계정·개인 토큰이 필요하지 않습니다. 라이선스·저작권 및 외부 구성요소 고지를 함께 보존하세요.

앱 **0.6.2**의 상태 저장·회전 잠금 처리 보완을 내장합니다. 기존 배포와 사용자 설치본은 보존합니다.

## 사용자 실행

`BoramRMS_Lite_0.6.2_Setup.exe` 더블클릭 → 바로가기·선택 서체 확인 → 설치 시작 → 앱 실행.
Python은 필요하지 않습니다. 기본 위치는 `%LOCALAPPDATA%\Programs\BoramRMSLite\App`이며 앱용 실행 환경이 포함됩니다.
기존 설치·신청서·다른 바로가기를 덮어쓰지 않습니다. 기존 사용자는 앱의 업데이트 기능을 사용하세요.

## 저장 잠금 보완

일시적인 Windows 공유 잠금은 제한 자동 재시도합니다. 회전을 처음부터 반복하지 않고 결과 파일 교체만 재시도합니다.
대기 중 외부 변경·동명 생성·읽기 전용은 덮어쓰지 않습니다. 실제 잠금을 건 프로세스를 식별한 것은 아닙니다.
기존 4색 테마·화면 배율·패널 분리·Space/Ctrl+Space·체크 즉시 저장 기능은 유지합니다.

## 선택 서체

서체 바이너리를 설치 EXE나 소스에 포함하지 않습니다. 선택하면 대상 PC에서 공식 Pretendard v1.3.9 배포를 받고 해시·라이선스를 검사합니다.
기본값은 미선택입니다. 공식 다운로드·4개 굵기 메모리 로딩 시험과 실제 사용자 서체 등록은 다른 검수입니다.
실제 사용자 프로필의 서체 등록·레지스트리 변경은 자동 개발 시험에서 하지 않습니다.

## 검증과 배포

새 배포는 기존 앱 시험과 L01~L14 잠금 재현을 포함한 최소 151개 시험, 설치 15개 시험, 설치된 앱 재시험, 공식 서체 다운로드·메모리 검수를 통과해야 생성됩니다.
공개용 산출물은 `dist/public/retry-1` 아래에 새로 생성합니다. 기존 `dist/releases/0.6.2`와 `dist/setup/0.6.2` 검수본, 처음 중단된 `dist/public/releases/0.6.2` 산출물은 덮어쓰지 않습니다.
통과 여부는 해당 실행에서 생성한 다음 결과로 확인합니다. 이전 버전의 시험 기록으로 대체하지 않습니다.

- `dist/public/retry-1/releases/0.6.2/BUILD_RESULT.json`
- `dist/public/retry-1/setup/0.6.2/SETUP_BUILD_RESULT.json`
- `dist/public/retry-1/releases/0.6.2/PUBLISH_RESULT.json`
- `dist/public/retry-1/setup/0.6.2/SETUP_PUBLISH_RESULT.json`
- `dist/public/retry-1/releases/0.6.2/PUBLIC_DOWNLOAD_RESULT.json`

```text
python release.py build
python installer/build_setup.py
# 실제 결과와 변경 소스 검토 후 지정 소스만 main에 커밋·push
python release.py publish
python installer/publish_setup.py
python verify_public_release.py
```

게시 대상은 공개 `ahnsungkwon/boram-rms-lite`의 새 v0.6.2입니다. 양쪽 소스 이력을 보존하며 강제 push하지 않습니다.
지정 공개 저장소·원격 main·배포 해시를 확인하고 기존 자산·태그를 덮어쓰지 않습니다.
새 패키지는 `LICENSE`, `THIRD_PARTY_NOTICES.md`, `PUBLIC_SHARING.md`와 기존 런타임 고지를 포함합니다.
실행 중 앱과 실제 고객 자료를 개발 시험에서 변경하지 않습니다.
설치 EXE는 코드서명이 없습니다. 백신·SmartScreen·조직 정책을 끄지 마세요. Windows 제거 항목은 등록하지 않습니다.
