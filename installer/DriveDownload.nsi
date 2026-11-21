Unicode true
Name "Диск Загрузки"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\DriveDownload"
RequestExecutionLevel user
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

!define ARP_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Mikhail068.DriveDownload"
InstallDirRegKey HKCU "${ARP_KEY}" "InstallLocation"
!define MUI_ICON "..\Assets\GoogleDrive.ico"
!define MUI_UNICON "..\Assets\GoogleDrive.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\DriveDownload.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить «Диск Загрузки»"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Function .onInit
    ${If} ${AtLeastWin10}
    ${AndIf} ${RunningX64}
        SetRegView 64
        SetShellVarContext current
    ${Else}
        MessageBox MB_OK|MB_ICONSTOP "Для установки нужна 64-разрядная Windows 10 или 11." /SD IDOK
        SetErrorLevel 2
        Abort
    ${EndIf}
FunctionEnd

Function un.onInit
    SetRegView 64
    SetShellVarContext current
FunctionEnd

Section "Диск Загрузки"
    SetOutPath "$INSTDIR"
    File /oname=DriveDownload.new "${APP_FILE}"

    ; Rename the complete payload into place without modifying a locked executable.
    System::Call 'kernel32::MoveFileExW(w "$INSTDIR\DriveDownload.new", w "$INSTDIR\DriveDownload.exe", i 9)i.r0'
    ${If} $0 == 0
        Delete "$INSTDIR\DriveDownload.new"
        MessageBox MB_OK|MB_ICONSTOP "Не удалось заменить файл программы. Закройте «Диск Загрузки» и повторите установку." /SD IDOK
        SetErrorLevel 3
        Abort
    ${EndIf}

    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateShortcut "$SMPROGRAMS\Диск Загрузки.lnk" "$INSTDIR\DriveDownload.exe"
    WriteRegStr HKCU "${ARP_KEY}" "DisplayName" "Диск Загрузки"
    WriteRegStr HKCU "${ARP_KEY}" "DisplayIcon" "$INSTDIR\DriveDownload.exe"
    WriteRegStr HKCU "${ARP_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${ARP_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegStr HKCU "${ARP_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
    WriteRegDWORD HKCU "${ARP_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${ARP_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
    ClearErrors
    Delete "$INSTDIR\DriveDownload.exe"
    ${If} ${Errors}
        MessageBox MB_OK|MB_ICONSTOP "Закройте «Диск Загрузки» и повторите удаление." /SD IDOK
        SetErrorLevel 3
        Abort
    ${EndIf}
    Delete "$SMPROGRAMS\Диск Загрузки.lnk"
    DeleteRegKey HKCU "${ARP_KEY}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
SectionEnd
