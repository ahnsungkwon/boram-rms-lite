# 보람 RMS Lite 0.1.0 — 구현 및 검증 기록

기준일: 2026-09-17 · 사용자 지시로 Spark가 아닌 대화의 assistant가 직접 작성한 독립 C#/WPF 앱.

## 확인된 결과

| 검증 | 실제 결과 |
|---|---|
| Release 빌드 | exit_code 0, 경고 0, 오류 0 |
| 개발 실행본 자체 검증 | PASS 32 / FAIL 0 |
| Windows x64 자체 포함 배포 | dotnet publish exit_code 0 |
| 배포 EXE 자체 검증 | PASS 32 / FAIL 0, exit_code 0 |
| 런타임 구성 | Microsoft.NETCore.App 8.0.31 + Microsoft.WindowsDesktop.App 8.0.31 includedFrameworks |
| UI 검수 | 합성 신청서를 로드한 실제 WPF 메인 화면 및 별도 창 렌더링, 주요 버튼 대비/글자 잘림 보완 |

개발 검증 기록: `tests-data\run_20260917_181224_399\SUMMARY.txt`
배포본 검증 기록: `tests-data\published\run_20260917_181535_965\SUMMARY.txt`
배포본 세부 JSON: 같은 디렉터리의 `test-results.json`.
두 SUMMARY 파일의 SHA-256: `3747b3726c84d32a167472f898e1f9f1f5fd156a177e302aedbe3401b4d70fc7`.

## 주요 확인 범위

폴더/행사 경계, 파일명 검증, 파생본 제외, 리네임 원본/가공본/카드/TXT 정합성, 중복 충돌 차단, 상태 제거, 입력 전/후 및 반복 구좌 변경, 부분 실패 복구, 외부 수정 보호, 관련 없는 TXT 보호, 리사이즈 원본 SHA/용량/픽셀/비율, PNG 투명도/JPEG 흰색 배경, EXIF 방향, 여러 페이지 제외, 회전 복구, 사본 연결, 보완자료 대체 이동/복구, 자료 없음/손상된 전화번호 XLSX, 로컬 쓰기 잠금, 설정 격리, 실제 UI 탭/검색/이미지 선택/패널 복귀/저장+다음/미저장 입력 보호, CP949 이름만 있는 TXT, 작업 중 취소, 복구 경로 범위를 시험했습니다.

트레이싱 테스트는 앱 자체 창의 스타일 전환·투명도·Topmost·해제를 검증했습니다. 다른 프로그램 위 실제 클릭 전달과 모든 멀티모니터 제스처를 자동화한 시험은 아닙니다.

## 수행하지 않은 검증과 배포 작업

실제 고객 신청서를 시험에 사용하지 않았습니다. 운영 전체 데이터, NAS/다른 PC와의 동시 작업, 장시간·대량 부하, 다양한 DPI/모니터, 사용자 한글 입력 전체 시나리오와 전체 기존 기능의 일대일 회귀 검수는 하지 않았습니다.
시스템 설치·바탕화면 바로가기·자동실행 등록·외부 배포·Git push·기존 RMS 덮어쓰기는 하지 않았습니다. 기존 RMS나 카카오톡/SmartPortal/ERP 서비스를 종료하거나 수정하지 않았습니다.

핵심 3영역 동작을 독립적으로 구현했으며, 기존 전체 앱의 모든 보조 기능이 동일하다는 보장은 하지 않습니다. 자세한 차이는 README_KO.md에 기록했습니다.

## 재현 명령 — 개발 PC용

```bat
dotnet build BoramRms.Lite.csproj -c Release --nologo
dotnet publish BoramRms.Lite.csproj -c Release -r win-x64 --self-contained true -o dist\BoramRMS_Lite_0.1.0_win-x64 --nologo
dist\BoramRMS_Lite_0.1.0_win-x64\BoramRms.Lite.exe --self-test tests-data\manual-check
```

자체 테스트는 지정 출력 폴더 안에 합성 자료와 백업·결과를 생성합니다. 실제 업무 폴더를 출력 위치로 사용하지 마세요.
