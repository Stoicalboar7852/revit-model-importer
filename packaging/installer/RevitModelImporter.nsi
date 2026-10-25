; Revit Model Importer - Windows installer (NSIS 3).
;
; Built by packaging/Build-Package.ps1 from the staged per-year packages:
;
;   makensis -DVERSION=1.0.0 -DSTAGE=<dist> -DROOT=<repository> -DOUTFILE=<exe> -DREVIT2025 -DREVIT2026 -DREVIT2027
;            packaging/installer/RevitModelImporter.nsi
;
; Installs for the current Windows user only, so no administrator rights are needed: the same files Install.ps1
; copies go to %AppData%\Autodesk\Revit\Addins\<year>, and the uninstaller (listed in Settings > Apps) goes to
; %LocalAppData%\Programs\Revit Model Importer.

Unicode true
ManifestDPIAware true
ManifestSupportedOS all
; zlib rather than lzma: NSIS's LZMA module is CPL-licensed (with a linking exception), its zlib module is zlib-licensed,
; and everything this project ships stays under permissive licences.
SetCompressor /SOLID zlib
RequestExecutionLevel user

!ifndef VERSION
  !error "Pass -DVERSION=<major.minor.patch>"
!endif
!ifndef STAGE
  !error "Pass -DSTAGE=<folder that holds Revit2025, Revit2026, Revit2027>"
!endif
!ifndef ROOT
  !error "Pass -DROOT=<repository root>"
!endif
!ifndef OUTFILE
  !define OUTFILE "RevitModelImporter-${VERSION}-Setup.exe"
!endif

!define PRODUCT "Revit Model Importer"
!define PUBLISHER "Revit Model Importer contributors"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\RevitModelImporter"
!define PRODUCT_KEY "Software\RevitModelImporter"
!define ADDINS "$APPDATA\Autodesk\Revit\Addins"

Name "${PRODUCT}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT}"
BrandingText "${PRODUCT} ${VERSION}"
ShowInstDetails hide
ShowUninstDetails hide

!include MUI2.nsh
!include LogicLib.nsh
!include Sections.nsh

Var RevitRunning
Var CommonAddins
Var FoundCount
Var Found2025
Var Found2026
Var Found2027
!ifdef REVIT2025
  Var Desc2025
!endif
!ifdef REVIT2026
  Var Desc2026
!endif
!ifdef REVIT2027
  Var Desc2027
!endif

; ----- Look and feel -------------------------------------------------------------------------------------------------

!define MUI_ICON "art/installer.ico"
!define MUI_UNICON "art/installer.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "art/wizard.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "art/wizard.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "art/header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "art/header.bmp"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC

; ----- Installer pages -----------------------------------------------------------------------------------------------

!define MUI_WELCOMEPAGE_TITLE "Welcome to ${PRODUCT} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Bring OBJ, FBX, glTF/GLB, DAE, STL, PLY, 3DS, 3MF and other 3D models into Revit as DirectShape elements, with their materials and textures.$\r$\n$\r$\nThis adds a Model Importer tab to the Revit versions you choose. It installs for your Windows account only and does not need administrator rights.$\r$\n$\r$\nPlease close Revit before you continue."
!insertmacro MUI_PAGE_WELCOME

!define MUI_PAGE_HEADER_TEXT "Choose Revit versions"
!define MUI_PAGE_HEADER_SUBTEXT "Select the Revit versions that should get the Model Importer tab."
!define MUI_COMPONENTSPAGE_TEXT_TOP "Versions found on this PC are already ticked. Click Install to continue."
!define MUI_COMPONENTSPAGE_TEXT_COMPLIST "Install for:"
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ComponentsLeave
!insertmacro MUI_PAGE_COMPONENTS

!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_TITLE "${PRODUCT} is installed"
!define MUI_FINISHPAGE_TEXT "Start Revit and open a project: the Model Importer tab is on the ribbon.$\r$\n$\r$\nThe add-in is not digitally signed, so the first time Revit asks whether to load it. Choose Always Load.$\r$\n$\r$\nTo remove it later, use Settings > Apps > Installed apps."
!insertmacro MUI_PAGE_FINISH

; ----- Uninstaller pages ---------------------------------------------------------------------------------------------

!define MUI_PAGE_CUSTOMFUNCTION_LEAVE un.ConfirmLeave
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "${PRODUCT} is removed"
!define MUI_FINISHPAGE_TEXT "The Model Importer tab is gone from Revit.$\r$\n$\r$\nElements you imported stay in your projects, and the texture folders next to them are left alone. Your importer settings are kept in %AppData%\RevitModelImporter in case you install it again."
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "English"

VIProductVersion "${VERSION}.0"
VIFileVersion "${VERSION}.0"
VIAddVersionKey /LANG=${LANG_ENGLISH} "ProductName" "${PRODUCT}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "FileDescription" "${PRODUCT} Setup"
VIAddVersionKey /LANG=${LANG_ENGLISH} "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "LegalCopyright" "Copyright (c) 2026 ${PUBLISHER}. MIT License."

; ----- Revit must be closed ------------------------------------------------------------------------------------------

; Sets $RevitRunning to 1 when a Revit.exe process exists. tasklist's CSV rows quote the image name; its
; "no tasks" message (which is translated) never contains "Revit.exe" in quotes.
!macro DEFINE_CHECK_REVIT_RUNNING UN
Function ${UN}CheckRevitRunning
  nsExec::ExecToStack '"$SYSDIR\tasklist.exe" /FI "IMAGENAME eq Revit.exe" /NH /FO CSV'
  Pop $0
  Pop $1
  StrCpy $RevitRunning 0
  ${If} $0 == 0
    StrCpy $2 0
    ${Do}
      StrCpy $3 $1 11 $2
      ${If} $3 == ""
        ${Break}
      ${EndIf}
      ${If} $3 == '"Revit.exe"'
        StrCpy $RevitRunning 1
        ${Break}
      ${EndIf}
      IntOp $2 $2 + 1
    ${Loop}
  ${EndIf}
FunctionEnd

; Asks the user to close Revit until it is closed ($0 = 1) or they cancel ($0 = 0). Silent runs cancel.
Function ${UN}WaitForRevitToClose
  ${Do}
    Call ${UN}CheckRevitRunning
    ${If} $RevitRunning == 0
      StrCpy $0 1
      Return
    ${EndIf}
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "Revit is running.$\r$\n$\r$\nSave your work and close Revit, then click Retry." /SD IDCANCEL IDRETRY retry
    StrCpy $0 0
    Return
    retry:
  ${Loop}
FunctionEnd
!macroend
!insertmacro DEFINE_CHECK_REVIT_RUNNING ""
!insertmacro DEFINE_CHECK_REVIT_RUNNING "un."

; ----- Install -------------------------------------------------------------------------------------------------------

!macro REVIT_SECTION YEAR
Section "Revit ${YEAR}" SecRevit${YEAR}
  ; Replace a previous version completely so no stale files remain.
  RMDir /r "${ADDINS}\${YEAR}\RevitModelImporter"
  SetOutPath "${ADDINS}\${YEAR}"
  File "${STAGE}/Revit${YEAR}/RevitModelImporter.addin"
  SetOutPath "${ADDINS}\${YEAR}\RevitModelImporter"
  File /r "${STAGE}/Revit${YEAR}/RevitModelImporter/*"
  WriteRegStr HKCU "${PRODUCT_KEY}" "Revit${YEAR}" "${VERSION}"
SectionEnd
!macroend

!ifdef REVIT2025
  !insertmacro REVIT_SECTION 2025
!endif
!ifdef REVIT2026
  !insertmacro REVIT_SECTION 2026
!endif
!ifdef REVIT2027
  !insertmacro REVIT_SECTION 2027
!endif

Section "-Uninstaller and documents"
  SetOutPath "$INSTDIR"
  File "/oname=README.md" "${ROOT}/README.md"
  File "/oname=LICENSE.txt" "${ROOT}/LICENSE"
  File "/oname=THIRD_PARTY_NOTICES.md" "${ROOT}/THIRD_PARTY_NOTICES.md"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  StrCpy $1 0
  !ifdef REVIT2025
    SectionGetSize ${SecRevit2025} $2
    ${If} ${SectionIsSelected} ${SecRevit2025}
      IntOp $1 $1 + $2
    ${EndIf}
  !endif
  !ifdef REVIT2026
    SectionGetSize ${SecRevit2026} $2
    ${If} ${SectionIsSelected} ${SecRevit2026}
      IntOp $1 $1 + $2
    ${EndIf}
  !endif
  !ifdef REVIT2027
    SectionGetSize ${SecRevit2027} $2
    ${If} ${SectionIsSelected} ${SecRevit2027}
      IntOp $1 $1 + $2
    ${EndIf}
  !endif

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Comments" "Add-in that imports 3D models into Revit 2025, 2026 and 2027."
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $1
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !ifdef REVIT2025
    !insertmacro MUI_DESCRIPTION_TEXT ${SecRevit2025} $Desc2025
  !endif
  !ifdef REVIT2026
    !insertmacro MUI_DESCRIPTION_TEXT ${SecRevit2026} $Desc2026
  !endif
  !ifdef REVIT2027
    !insertmacro MUI_DESCRIPTION_TEXT ${SecRevit2027} $Desc2027
  !endif
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ----- Finding Revit -------------------------------------------------------------------------------------------------

; Revit is "found" when its program, its all-users add-ins folder, its per-user profile folder or an earlier
; install of this add-in exists.
!macro DETECT YEAR
  StrCpy $Found${YEAR} 0
  ${If} ${FileExists} "$PROGRAMFILES64\Autodesk\Revit ${YEAR}\Revit.exe"
  ${OrIf} ${FileExists} "$CommonAddins\${YEAR}\*.*"
  ${OrIf} ${FileExists} "$APPDATA\Autodesk\Revit\Autodesk Revit ${YEAR}\*.*"
  ${OrIf} ${FileExists} "${ADDINS}\${YEAR}\RevitModelImporter.addin"
    StrCpy $Found${YEAR} 1
    IntOp $FoundCount $FoundCount + 1
  ${EndIf}
!macroend

; Ticks the versions that were found. If none was found (Revit installed somewhere unusual), every version is ticked.
!macro APPLY_DETECTION YEAR SECTION
  ${If} $Found${YEAR} == 1
    !insertmacro SelectSection ${SECTION}
    StrCpy $Desc${YEAR} "Revit ${YEAR} was found on this PC."
  ${ElseIf} $FoundCount == 0
    !insertmacro SelectSection ${SECTION}
    StrCpy $Desc${YEAR} "Revit was not found in its usual places, so every version is ticked. Untick the ones you do not use."
  ${Else}
    !insertmacro UnselectSection ${SECTION}
    SectionSetText ${SECTION} "Revit ${YEAR}  (not found)"
    StrCpy $Desc${YEAR} "Revit ${YEAR} was not found in its usual places. Tick it if it is installed somewhere else."
  ${EndIf}
!macroend

Function .onInit
  SetShellVarContext all
  StrCpy $CommonAddins "$APPDATA\Autodesk\Revit\Addins"
  SetShellVarContext current

  StrCpy $FoundCount 0
  !insertmacro DETECT 2025
  !insertmacro DETECT 2026
  !insertmacro DETECT 2027
  !ifdef REVIT2025
    !insertmacro APPLY_DETECTION 2025 ${SecRevit2025}
  !endif
  !ifdef REVIT2026
    !insertmacro APPLY_DETECTION 2026 ${SecRevit2026}
  !endif
  !ifdef REVIT2027
    !insertmacro APPLY_DETECTION 2027 ${SecRevit2027}
  !endif

  ${If} ${Silent}
    Call WaitForRevitToClose
    ${If} $0 == 0
      SetErrorLevel 2
      Quit
    ${EndIf}
  ${EndIf}
FunctionEnd

Function ComponentsLeave
  StrCpy $1 0
  !ifdef REVIT2025
    ${If} ${SectionIsSelected} ${SecRevit2025}
      IntOp $1 $1 + 1
    ${EndIf}
  !endif
  !ifdef REVIT2026
    ${If} ${SectionIsSelected} ${SecRevit2026}
      IntOp $1 $1 + 1
    ${EndIf}
  !endif
  !ifdef REVIT2027
    ${If} ${SectionIsSelected} ${SecRevit2027}
      IntOp $1 $1 + 1
    ${EndIf}
  !endif
  ${If} $1 == 0
    MessageBox MB_OK|MB_ICONINFORMATION "Tick at least one Revit version."
    Abort
  ${EndIf}

  Call WaitForRevitToClose
  ${If} $0 == 0
    Abort
  ${EndIf}
FunctionEnd

; ----- Uninstall -----------------------------------------------------------------------------------------------------

Function un.onInit
  SetShellVarContext current
  ${If} ${Silent}
    Call un.WaitForRevitToClose
    ${If} $0 == 0
      SetErrorLevel 2
      Quit
    ${EndIf}
  ${EndIf}
FunctionEnd

Function un.ConfirmLeave
  Call un.WaitForRevitToClose
  ${If} $0 == 0
    Abort
  ${EndIf}
FunctionEnd

!macro REMOVE_YEAR YEAR
  Delete "${ADDINS}\${YEAR}\RevitModelImporter.addin"
  RMDir /r "${ADDINS}\${YEAR}\RevitModelImporter"
!macroend

Section "Uninstall"
  ; Every supported year, also ones installed by Install.ps1 or an older setup.
  !insertmacro REMOVE_YEAR 2025
  !insertmacro REMOVE_YEAR 2026
  !insertmacro REMOVE_YEAR 2027

  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "${PRODUCT_KEY}"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\THIRD_PARTY_NOTICES.md"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
