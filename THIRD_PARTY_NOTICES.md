# Third-party notices / 외부 구성요소 안내

보람 RMS Lite의 프로젝트 고유 소스·문서·배포본에는 루트 `LICENSE`의 MIT License를 적용합니다. 외부 구성요소의 저작권 및 원래 라이선스는 변경하지 않습니다.

## Microsoft .NET / Windows Desktop runtime

독립 실행 배포본에는 Microsoft .NET 및 Windows Desktop 실행 구성요소가 포함됩니다. 해당 구성요소의 저작권은 .NET Foundation 및 해당 기여자에게 있으며, 각 원본 라이선스·제3자 고지를 유지해야 합니다. 보람 RMS Lite의 저작권 표시가 Microsoft 또는 제3자의 표시를 대체하지 않습니다.

- Runtime license: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- Runtime third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- WPF license: https://github.com/dotnet/wpf/blob/main/LICENSE.TXT
- Windows Forms license: https://github.com/dotnet/winforms/blob/main/LICENSE.TXT

재배포 시 실제 포함한 런타임 버전에 해당하는 원문 라이선스·고지를 함께 보존하세요. 새 공개 패키지는 복원된 런타임 패키지가 제공하는 원문을 `licenses/`에 포함하고 `licenses/RUNTIME_NOTICES.json`에 구성요소 버전·파일 해시를 기록합니다. 라이선스 원문 누락은 항상 차단합니다.

확인된 Windows Desktop 8.0.31 NuGet 패키지는 별도 제3자 고지 파일 없이 `LICENSE`를 제공합니다. 해당 원본 LICENSE 및 NuGet 명세의 고정 SHA-256이 모두 일치하고 다른 고지 파일이 없을 때만 이 구성을 허용하며, manifest에 `separateNoticeProvided: false`와 명세 해시를 기록합니다. 존재하지 않는 고지를 만들거나 다른 패키지의 고지로 대체하지 않습니다. .NETCore 패키지의 라이선스와 제3자 고지는 모두 포함하고, 새로운 버전이나 다른 구성은 다시 확인하기 전까지 차단합니다.

## Pretendard — optional download

Pretendard는 SIL Open Font License 1.1에 따릅니다. 프로젝트의 MIT 적용으로 서체 라이선스가 MIT로 바뀌지 않습니다. 서체 바이너리는 이 저장소나 설치 EXE에 포함하지 않으며, 사용자가 선택하면 공식 배포처에서 다운로드합니다.

Official source and license: https://github.com/orioncactus/pretendard/blob/main/LICENSE

## Windows fonts and user documents

Windows 기본 서체는 Windows에서 제공되는 글꼴을 사용하며 이 저장소에서 재배포하지 않습니다. 사용자가 앱으로 여는 신청서·개인정보·로고·타인의 자료는 이 프로젝트의 소프트웨어 라이선스에 따라 자동 공개되거나 사용 허락되는 것이 아닙니다.
