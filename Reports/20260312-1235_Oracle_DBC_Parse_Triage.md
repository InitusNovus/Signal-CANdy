## 📝 작업 요약

RUN_ID: 20260312-1235

- `hyundai_kia_generic.dbc`, `toyota_2017_ref_pt.dbc`, `vw_meb.dbc`에 대해 cantools 파싱 실패 원인과 Signal-CANdy 파싱 결과를 재현했다.
- 결론은 Hyundai는 malformed comment 구문 때문에 `dbc_malformed`, Toyota/VW는 cantools가 extended-frame 표현을 표준 11-bit ID로 해석해 실패한 `cantools_incompatible`이다.

## 🛠 변경 상세

- 수정: `.sisyphus/notepads/oracle-failure-resolution/wave1_findings.md`
  - 세 DBC의 cantools 정확한 오류 메시지, Signal-CANdy 실행 결과, 라인 참조, 최종 분류를 append 했다.
- 분석 근거:
  - `tests/oracle/ORACLE_RESULTS.md`
  - `tests/oracle/vendor_dbc/hyundai_kia_generic.dbc:1655`
  - `tests/oracle/vendor_dbc/hyundai_kia_generic.dbc:1656`
  - `tests/oracle/vendor_dbc/hyundai_kia_generic.dbc:1657`
  - `tests/oracle/vendor_dbc/toyota_2017_ref_pt.dbc:387`
  - `tests/oracle/vendor_dbc/vw_meb.dbc:2074`
- 분류 결과:
  - `hyundai_kia_generic.dbc` -> `dbc_malformed`
  - `toyota_2017_ref_pt.dbc` -> `cantools_incompatible`
  - `vw_meb.dbc` -> `cantools_incompatible`
- ROADMAP 갱신: 없음

## ✅ 테스트 결과

- cantools 버전: `41.2.1`
- cantools 재현:
  - `python -c "import cantools; cantools.database.load_file('tests/oracle/vendor_dbc/hyundai_kia_generic.dbc')"`
    - 실패: `UnsupportedDatabaseFormatError: DBC: "Invalid syntax at line 1656, column 5: \"CM_ >>!<<145 \"Contains signal with accelerator pedal press. Used by fuel cell hydrogen-powered (FCEV) cars such as the 2021 Hyundai Nexo.\";\""`
  - `python -c "import cantools; cantools.database.load_file('tests/oracle/vendor_dbc/toyota_2017_ref_pt.dbc')"`
    - 실패: `UnsupportedDatabaseFormatError: DBC: "Standard frame id 0x40140639 is more than 11 bits in message BDB1F01_14."`
  - `python -c "import cantools; cantools.database.load_file('tests/oracle/vendor_dbc/vw_meb.dbc')"`
    - 실패: `UnsupportedDatabaseFormatError: DBC: "Standard frame id 0x12dd54a7 is more than 11 bits in message MEB_Camera_04."`
- Signal-CANdy 재현:
  - `dotnet run --project src/Generator -- --dbc tests/oracle/vendor_dbc/hyundai_kia_generic.dbc --out tmp/test_hyundai --config examples/config.yaml`
    - 성공: `Code generation successful.`
    - 생성 확인: `tmp/test_hyundai/include/`, `tmp/test_hyundai/src/`
  - `dotnet run --project src/Generator -- --dbc tests/oracle/vendor_dbc/toyota_2017_ref_pt.dbc --out tmp/test_toyota --config examples/config.yaml`
    - 성공: `Code generation successful.`
    - 생성 확인: `tmp/test_toyota/include/bdb1f01_14.h`
  - `dotnet run --project src/Generator -- --dbc tests/oracle/vendor_dbc/vw_meb.dbc --out tmp/test_vw --config examples/config.yaml`
    - 성공: `Code generation successful.`
    - 생성 확인: `tmp/test_vw/include/meb_camera_04.h`
- 진단:
  - Hyundai는 `CM_ 145 ...;` / `CM_ 512 ...;` 구문이 object kind 없이 작성되어 DBC comment 문법 자체가 깨져 있다.
  - Toyota/VW는 corpus가 사용하는 extended-frame ID 표현을 cantools가 수용하지 못해 표준 11-bit ID 초과 오류로 중단한다.

## ⏭ 다음 계획

- Task 7 문서화 단계에서 이 분류를 oracle failure taxonomy에 반영한다.
- 필요 시 oracle 파이프라인에 `dbc_malformed` / `cantools_incompatible` 사전 분류 로직을 추가해 cantools parse 단계 실패를 known-skip으로 전환한다.
