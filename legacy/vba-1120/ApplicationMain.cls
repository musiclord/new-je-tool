VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ApplicationMain"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class:    ApplicationMain
' Purpose:  主要程式，作為表單和類別的溝通主體
' Methods:
'===============================================================================
Private WithEvents vProject As ViewProject
Attribute vProject.VB_VarHelpID = -1
Private WithEvents vMain As ViewMain
Attribute vMain.VB_VarHelpID = -1
Private WithEvents vImport As ViewImport
Attribute vImport.VB_VarHelpID = -1
Private WithEvents vMapJe As ViewImportMapJe
Attribute vMapJe.VB_VarHelpID = -1
Private WithEvents vMapTb As ViewImportMapTb
Attribute vMapTb.VB_VarHelpID = -1
Private WithEvents vValidation As ViewValidation
Attribute vValidation.VB_VarHelpID = -1
Private WithEvents vFilterRouter As ViewFilterRouter
Attribute vFilterRouter.VB_VarHelpID = -1
Private WithEvents vFilterCurrent As ViewFilterCurrent
Attribute vFilterCurrent.VB_VarHelpID = -1
Private WithEvents vFilterLegacy As ViewFilterLegacy
Attribute vFilterLegacy.VB_VarHelpID = -1
Private WithEvents vAddCriteria As ViewFilterAddCriteria
Attribute vAddCriteria.VB_VarHelpID = -1
Private WithEvents vAddSql As ViewFilterAddSql
Attribute vAddSql.VB_VarHelpID = -1

Private WithEvents vExport As ViewExport
Attribute vExport.VB_VarHelpID = -1
'--
Private fso As FileSystemObject
Private calendar As ServiceDateDimension
Private context As ContextManager
Private projector As ServiceProject
Private importer As ServiceImport
Private exporter As ServiceExport
Private reporter As ServiceReport
Private validator As ServiceValidation
Private mapper As ServiceAccountMapping
Private filter As ServiceFilter

'===============================================================================
'-- Application
'===============================================================================
Public Sub Initialize()
    Set vProject = New ViewProject
    Set vMain = New ViewMain
    Set vImport = New ViewImport
    Set vMapJe = New ViewImportMapJe
    Set vMapTb = New ViewImportMapTb
    Set vValidation = New ViewValidation
    Set vFilterRouter = New ViewFilterRouter
    Set vFilterCurrent = New ViewFilterCurrent
    Set vFilterLegacy = New ViewFilterLegacy
    Set vAddCriteria = New ViewFilterAddCriteria
    Set vAddSql = New ViewFilterAddSql
    Set vExport = New ViewExport
    '--
    Set fso = New FileSystemObject
    Set calendar = New ServiceDateDimension
    Set context = New ContextManager
    Set projector = New ServiceProject
    Set importer = New ServiceImport
    Set exporter = New ServiceExport
    Set reporter = New ServiceReport
    Set validator = New ServiceValidation
    Set mapper = New ServiceAccountMapping
    Set filter = New ServiceFilter
    '--
    Call context.Initialize
End Sub
Public Sub Run()
    Call projector.Initialize(context)
    Call vProject.Initialize
    vProject.Show vbModeless
End Sub

Private Sub vAddSql_AddSqlRequested()
    Dim sql As String
    sql = vAddSql.txtbSql.value
End Sub

'===============================================================================
'-- vProject
'===============================================================================
Private Sub vProject_NewProject(ByVal path As String)
    '---------------------------------------------------------------------------
    ' 依據輸入路徑創建專案目錄
    '---------------------------------------------------------------------------
    projector.NewProject path
End Sub
Private Sub vProject_SelectProject(ByVal path As String)
    '---------------------------------------------------------------------------
    ' 依據輸入路徑載入專案目錄並開啟主要表單介面 ViewMain
    '---------------------------------------------------------------------------
    projector.SelectProject path
    vProject.Hide
    Call vMain.Initialize(fso.GetFolder(path).name)
    vMain.Show vbModeless
End Sub

'===============================================================================
'-- vMain
'===============================================================================

'-------------------------------------------------------------------------------
' 初始化步驟一所需的物件並開啟表單介面 ViewImport
'-------------------------------------------------------------------------------
Private Sub vMain_DoStep1()
    Call importer.Initialize(context)
    Call calendar.Initialize(context)
    '--
    Call vImport.Initialize
    ' [New] Sync View State with DB Reality on Startup
    ' 這是為了防止使用者關閉視窗後重新打開，狀態被重置
    If Not context.DbInput Is Nothing Then
        vImport.SetJeState context.DbInput.TableExists(context.DbSchema.JE.name)
        vImport.SetTbState context.DbInput.TableExists(context.DbSchema.TB.name)
        vImport.SetCalendarState context.DbInput.TableExists(context.DbSchema.DateDimension.name)
    End If
    vImport.Show vbModeless
End Sub

'-------------------------------------------------------------------------------
' 初始化步驟二所需的物件並開啟表單介面 ViewValidation
'-------------------------------------------------------------------------------
Private Sub vMain_DoStep2()
    Call validator.Initialize(context)
    Call mapper.Initialize(context)
    '--
    Call vValidation.Initialize
    vValidation.Show vbModeless
End Sub

'---------------------------------------------------------------------------
' 初始化步驟三所需的物件並開啟臨時表單介面 ViewFilterRouter 來路由篩選模式
'---------------------------------------------------------------------------
Private Sub vMain_DoStep3()
    Call filter.Initialize(context)
    '--
    vFilterRouter.Show vbModeless
End Sub

'---------------------------------------------------------------------------
' 初始化步驟四所需的物件並開啟表單介面 ViewExport
'---------------------------------------------------------------------------
Private Sub vMain_DoStep4()
    Call reporter.Initialize(context)
    Call exporter.Initialize(context)
    '--
    Call vExport.Initialize
    vExport.Show vbModeless
End Sub

'---------------------------------------------------------------------------
' 關閉程序並釋放資源
'---------------------------------------------------------------------------
Private Sub vMain_ExitApplication()
    If Not context Is Nothing Then
        If Not context.DbInput Is Nothing Then
            context.DbInput.Disconnect
            Set context.DbInput = Nothing
        End If
        If Not context.DbValid Is Nothing Then
            context.DbValid.Disconnect
            Set context.DbValid = Nothing
        End If
        Set context = Nothing
    End If
    Application.Quit
End Sub

'===============================================================================
'-- vImport
'===============================================================================
Private Sub vImport_BeepBoop()
    '//WARNING: BEEP BOOP
    '//WARNING: BEEP BOOP
    '//WARNING: BEEP BOOP
    Dim testString As String
    testString = context.DbSchema.JE.name
    Debug.Print "BEEP BOOP:" & context.DbSchema.JE.name
    MsgBox testString, vbInformation, testString
End Sub

'-------------------------------------------------------------------------------
' 使用資料庫匯入精靈處理 JE 匯入
'-------------------------------------------------------------------------------
Private Sub vImport_ImportJournalEntries(ByVal Format As String)
    importer.dType = Format
    importer.OpenWizard "je"
    vImport.lblImportStatus.Caption = "匯入資料表: " & context.DbSchema.JE.name
    ' [New] Update View State
    vImport.SetJeState context.DbInput.TableExists(context.DbSchema.JE.name)
End Sub

'-------------------------------------------------------------------------------
' 使用資料庫匯入精靈處理 TB 匯入
'-------------------------------------------------------------------------------
Private Sub vImport_ImportTrialBalance(ByVal Format As String)
    importer.dType = Format
    importer.OpenWizard "tb"
    vImport.lblImportStatus.Caption = "匯入資料表: " & context.DbSchema.TB.name
    ' [New] Update View State
    vImport.SetTbState context.DbInput.TableExists(context.DbSchema.TB.name)
End Sub

'-------------------------------------------------------------------------------
' 開啟JE欄位映射的介面
'-------------------------------------------------------------------------------
Private Sub vImport_JeFieldMappingRequested()
    '檢查是否有匯入 JE 資料表
    If Not context.DbInput.TableExists(context.DbSchema.JE.name) Then
        MsgBox "尚未匯入 JE 資料表", vbExclamation, "JE欄位映射"
        vImport.btnImportJe.SetFocus
        Exit Sub
    End If
    '初始化映射JE的表單物件
    Call vMapJe.Initialize(context.DbInput)
    vMapJe.Show vbModeless
End Sub

'-------------------------------------------------------------------------------
' 開啟TB欄位映射的介面
'-------------------------------------------------------------------------------
Private Sub vImport_TbFieldMappingRequested()
    '檢查是否有匯入 TB 資料表
    If Not context.DbInput.TableExists(context.DbSchema.TB.name) Then
        MsgBox "尚未匯入 TB 資料表", vbExclamation, "TB欄位映射"
        vImport.btnImportTb.SetFocus
        Exit Sub
    End If
    '初始化映射TB的表單物件
    Call vMapTb.Initialize(context.DbInput)
    vMapTb.Show vbModeless
End Sub

'-------------------------------------------------------------------------------
' 更新/覆寫日期維度表
'-------------------------------------------------------------------------------
Private Sub vImport_UpdateDateDimensionRequested(ByVal dto As DataTransferObject)
    '-- 先建立日期維度表
    context.info.PeriodStart = dto.PeriodStart
    context.info.PeriodEnd = dto.PeriodEnd
    Call calendar.CreateDateDimensionTable
    '-- 準備物件
    Dim ws As Worksheet
    Dim dataRange As String
    Dim types As Variant
    Dim fieldNames As Variant
    dataRange = "A2:B200"
    types = Array("DATE", "TEXT(50)")
    fieldNames = Array("DateValue", "Description")
    '-- 匯入補班日資料
    Set ws = SHT_MAKEUPDAYS
    Dim rsMakeupDays As DAO.Recordset
    Set rsMakeupDays = importer.ImportFromSheet( _
                                    ws, _
                                    dataRange, _
                                    types, _
                                    fieldNames _
                                )
    If Not rsMakeupDays Is Nothing And Not rsMakeupDays.EOF Then
        Call calendar.MergeMakeupDays(rsMakeupDays)
    Else
        Debug.Print "  - MakeupDaySheet 無資料或匯入失敗"
    End If
    '-- 匯入假日資料
    Set ws = SHT_HOLIDAYS
    Dim rsHolidays As DAO.Recordset
    Set rsHolidays = importer.ImportFromSheet( _
                                    ws, _
                                    dataRange, _
                                    types, _
                                    fieldNames _
                                )
    If Not rsHolidays Is Nothing And Not rsHolidays.EOF Then
        Call calendar.MergeHolidays(rsHolidays)
    Else
        Debug.Print "  - HolidaySheet 無資料或匯入失敗"
    End If
    '-- 匯入週末資料
    Call calendar.MergeWeekends(dto.weekendIndices)
    '-- 成功訊息
    MsgBox "日期維度已完成設定", vbInformation, "日期設定"
    ' [NEW] 回推前端狀態，避免 btnExit 誤判
    vImport.SetCalendarState context.DbInput.TableExists(context.DbSchema.DateDimension.name)
End Sub
'-------------------------------------------------------------------------------
'//WARNING: ONLY FOR TESTING
'//NOTE:只負責觸發前端的操作
'-------------------------------------------------------------------------------
Private Sub vImport_TestDefaultRequested()
    Call vMapJe.Initialize(context.DbInput)
    Call vMapJe.ApplyTestDefaults
    Call vMapTb.Initialize(context.DbInput)
    Call vMapTb.ApplyTestDefaults
End Sub

'-------------------------------------------------------------------------------
' 接收組裝好的 DTO ，處理匯入表單提交並更新 Context
'-------------------------------------------------------------------------------
Private Sub vImport_Submitted(ByVal dto As DataTransferObject)
    context.info.CompanyName = dto.CompanyName
    context.info.PrepStartDate = dto.PrepStartDate
End Sub

'===============================================================================
'-- vMapJe
'===============================================================================

'-------------------------------------------------------------------------------
' 套用表單 MapJe 的欄位映射
'-------------------------------------------------------------------------------
Private Sub vMapJe_ApplyField(ByVal dict As Dictionary, ByVal amountMethod As Long)
    context.DbSchema.JE.method = amountMethod
    If Not importer.ApplyJe(dict) Then
        vImport.lblFieldMappingStatus.Caption = " JE FAILED"
        MsgBox "JE 套用欄位失敗", vbExclamation, "JE 套用欄位"
    Else
        vImport.lblFieldMappingStatus.Caption = "JE PASS"
    End If
End Sub

'===============================================================================
'-- vMapTb
'===============================================================================

'-------------------------------------------------------------------------------
' 套用表單 MapTb 的欄位映射
'-------------------------------------------------------------------------------
Private Sub vMapTb_ApplyField(ByVal dict As Dictionary, ByVal amountMethod As Long)
    context.DbSchema.TB.method = amountMethod
    If Not importer.ApplyTb(dict) Then
        vImport.lblFieldMappingStatus.Caption = "TB FAILED"
        MsgBox "TB套用欄位失敗", vbExclamation, "TB套用欄位"
    Else
        vImport.lblFieldMappingStatus.Caption = "TB PASS"
    End If
End Sub

'===============================================================================
'-- vValidation
'===============================================================================

'-------------------------------------------------------------------------------
' 執行完整性測試
'-------------------------------------------------------------------------------
Private Sub vValidation_CheckCompleteness()
    Dim succeeded As Boolean
    Dim msg As String

    succeeded = validator.CheckCompleteness()
    If succeeded Then
        msg = "所有期間都有 JE。"
    Else
        msg = "請檢視 COMPLETENESS_DETAIL。"
    End If
    vValidation.ReportTestResult "完整性檢查", succeeded, msg
End Sub

'-------------------------------------------------------------------------------
' 執行借貸不平測試
'-------------------------------------------------------------------------------
Private Sub vValidation_CheckDocumentBalance()
    Dim succeeded As Boolean
    Dim msg As String

    succeeded = validator.CheckDocumentBalance()
    If succeeded Then
        msg = "借貸金額平衡。"
    Else
        msg = "發現不平衡，請檢視 DOCUMENT_BALANCE_DETAIL。"
    End If
    vValidation.ReportTestResult "憑證借貸平衡", succeeded, msg
End Sub

'-------------------------------------------------------------------------------
' 檢查空值分錄
'-------------------------------------------------------------------------------
Private Sub vValidation_CheckNullRecords()
    Dim succeeded As Boolean
    Dim msg As String

    succeeded = validator.CheckNullRecords()
    If succeeded Then
        msg = "未偵測到 Null 欄位。"
    Else
        msg = "有 Null 欄位，請檢視 NULL_RECORDS_DETAIL。"
    End If
    vValidation.ReportTestResult "Null Records 檢查", succeeded, msg
End Sub

'-------------------------------------------------------------------------------
' 執行可靠性測試
'-------------------------------------------------------------------------------
Private Sub vValidation_CheckINF()
    Dim succeeded As Boolean
    Dim msg As String

    succeeded = validator.CheckINF()
    If succeeded Then
        msg = "INF 檢查通過。"
    Else
        msg = "INF 清單有異常，請檢查 LOG_INF_DETAIL。"
    End If
    vValidation.ReportTestResult "INF 檢查", succeeded, msg
End Sub

'-------------------------------------------------------------------------------
' 開啟科目配對工作表
' Workflow:
'     1. 指定工作表物件 (AccountMappingSheet)
'     2. 由 mapper 初始化工作表
'-------------------------------------------------------------------------------
Private Sub vValidation_ShowAccountMapping()
    '-- 檢查必要資料表
    If Not context.DbValid.TableExists(context.DbSchema.Tables.CompletenessDetail) Then
        MsgBox "未找到完整性測試明細資料表", vbExclamation, "查閱科目配對"
        Exit Sub
    End If
    '-- 指定工作表物件
    Dim ws As Worksheet
    Set ws = SHT_ACCOUNT_MAPPING
    '-- 產出科目配對工作表
    Call mapper.ExportTemplate(ws)
End Sub

'-------------------------------------------------------------------------------
' 匯入科目配對工作表至資料庫
' Workflow:
'     1. 指定工作表物件 (AccountMappingSheet)
'     2. 由 importer 傳回記錄集
'     3. 由 mapper 將記錄集會入至資料庫
'-------------------------------------------------------------------------------
Private Sub vValidation_ImportAccountMapping()
    '-- 定義工作表物件
    Dim ws As Worksheet
    Set ws = SHT_ACCOUNT_MAPPING
    '-- 定義資料範圍
    Dim lastRow As Long
    lastRow = ws.Cells(ws.Rows.Count, 1).End(xlUp).row
    If lastRow < 2 Then
        MsgBox "科目配對工作表沒有資料", vbInformation, "科目配對"
        Exit Sub
    End If
    Dim dataRange As String
    dataRange = "A1:C" & lastRow
    
    '-- 定義欄位型別
    Dim fieldTypes As Variant
    fieldTypes = Array("TEXT(50)", "TEXT(50)", "TEXT(50)")
    '-- 定義記錄集物件
    Dim rs As DAO.Recordset
    Set rs = importer.ImportFromSheet(ws, dataRange, fieldTypes)
    
    '-- 匯入科目配對表至資料庫
    If Not mapper.ImportAccountMapping(rs) Then
        MsgBox "科目配對失敗", vbExclamation, "科目配對"
    Else
        MsgBox "科目配對成功", vbInformation, "科目配對"
    End If
End Sub

'-------------------------------------------------------------------------------
' 接收組裝好的 DTO 物件，更新 Context 狀態
'-------------------------------------------------------------------------------
Private Sub vValidation_Submitted(ByVal dto As DataTransferObject)
    '...
End Sub

'===============================================================================
'-- vFilterRouter
'===============================================================================
Private Sub vFilterRouter_RouteToCurrent()
    Call vFilterCurrent.Initialize
    vFilterCurrent.Show vbModeless ' <-- 使用新版本的篩選條件
End Sub

Private Sub vFilterRouter_RouteToLegacy()
    Call vFilterLegacy.Initialize(context.DbInput)
    vFilterLegacy.Show vbModeless ' <-- 使用舊版本的篩選條件
End Sub

'===============================================================================
'-- vFilterLegacy
'===============================================================================
' 組成 SQL 查詢，執行所有條件集合
'-------------------------------------------------------------------------------
Private Sub vFilterLegacy_ExecuteCriteriaRequested()
    '1. 將表單上的狀態(所有條件組合)傳遞給 Filter 服務
    Dim states As Dictionary
    Set states = vFilterLegacy.criteriaStates
    
    '2. 處理並轉換這些狀態為 SQL 條件物件
    filter.ProcessCriteriaSet states, "legacy"
    
    '3. 執行批次篩選，結果表前綴為 FILTER_RESULT_
    '   例如: FILTER_RESULT_條件_1
    filter.ExecuteBatch "FILTER_RESULT"
    
    MsgBox "篩選執行完畢！", vbInformation, "Legacy Filter"
End Sub

'-------------------------------------------------------------------------------
'...
'-------------------------------------------------------------------------------
Private Sub vFilterLegacy_ShowCriteriaRequested()
    '...
    
End Sub

'-------------------------------------------------------------------------------
'...
'-------------------------------------------------------------------------------
Private Sub vFilterLegacy_Submitted(ByVal dto As DataTransferObject)
    '...
End Sub

'===============================================================================
'-- vFilterCurrent
'===============================================================================
' 組成 SQL 查詢，執行所有條件集合
'-------------------------------------------------------------------------------
Private Sub vFilterCurrent_ExecuteCriteriaRequested()
    '1. 將介面的狀態設給服務層讀取
    Dim states As Dictionary
    Set states = vFilterCurrent.criteriaStates
    
    '2. 處理並映射條件集合
    'filter.ProcessCriteriaSet(states, "current")
    
    '3. 執行所有篩選條件
    'filter.ExecuteBatch("FILTER_RESULT")
    
End Sub

'-------------------------------------------------------------------------------
' 總覽條件組合詳細
'-------------------------------------------------------------------------------
Private Sub vFilterCurrent_OverviewCriteria()
    ' Archived empty handler.
End Sub

'-------------------------------------------------------------------------------
' 新增條件組合
'-------------------------------------------------------------------------------
Private Sub vFilterCurrent_AddCriteriaRequested()
    
End Sub

'-------------------------------------------------------------------------------
' 新增條件，打開 ViewFilterAddCriteria
'-------------------------------------------------------------------------------
Private Sub vFilterCurrent_AddCriterionRequested()

End Sub

'-------------------------------------------------------------------------------
' 要求打開 ViewFilterAddSql
'-------------------------------------------------------------------------------
Private Sub vFilterCurrent_AddSqlRequested()
    Call vAddSql.Initialize '<--nothing yet
    vAddSql.Show vbModeless
End Sub

'-------------------------------------------------------------------------------
' 移除所選的條件組合
'-------------------------------------------------------------------------------
Private Sub vFilterCurrent_RemoveCriteriaRequested()

End Sub

'-------------------------------------------------------------------------------
' 移除所選的條件
'-------------------------------------------------------------------------------
Private Sub vFilterCurrent_RemoveCriterionRequested()

End Sub

'-------------------------------------------------------------------------------
' ...
'-------------------------------------------------------------------------------
Private Sub vFilterCurrent_Submitted(dto As DataTransferObject)

End Sub

'===============================================================================
'-- vAddCriteria
'===============================================================================

'-------------------------------------------------------------------------------
' ...
'-------------------------------------------------------------------------------
Private Sub vAddCriteria_AddCriteria()

End Sub

'-------------------------------------------------------------------------------
' ...
'-------------------------------------------------------------------------------
Private Sub vAddCriteria_Cancel()

End Sub

'-------------------------------------------------------------------------------
' ...
'-------------------------------------------------------------------------------
Private Sub vAddCriteria_SelectTypeDate()

End Sub

'-------------------------------------------------------------------------------
' ...
'-------------------------------------------------------------------------------
Private Sub vAddCriteria_SelectTypeKeyword()

End Sub

'-------------------------------------------------------------------------------
' ...
'-------------------------------------------------------------------------------
Private Sub vAddCriteria_SelectTypeNumerical()

End Sub

'===============================================================================
'-- vExport
'===============================================================================

'-------------------------------------------------------------------------------
' 匯出全部完整的報表至專案目錄
'-------------------------------------------------------------------------------
Private Sub vExport_ExportWp()
    exporter.ExportWp
End Sub

'-------------------------------------------------------------------------------
' 產出專案摘要的報表
'-------------------------------------------------------------------------------
Private Sub vExport_ShowEngagementOverview()
    '建立必要報表
    reporter.GenerateEngagementOverview
    '輸出報表
    exporter.ShowEngagementOverview
End Sub

'-------------------------------------------------------------------------------
' 產出驗證結果的報表
'-------------------------------------------------------------------------------
Private Sub vExport_ShowValidationReport()
    '建立必要報表
    reporter.GenerateEngagementOverview
    reporter.GenerateDataOverview
    reporter.GenerateValidationOverview
    '輸出報表
    exporter.ShowValidationReport
End Sub

'-------------------------------------------------------------------------------
' 產出完整性測試的報表
'-------------------------------------------------------------------------------
Private Sub vExport_ShowCompletenessReport()
    '建立必要報表
    reporter.GenerateEngagementOverview
    reporter.GenerateCompletenessDetail
    '輸出報表
    exporter.ShowCompletenessReport
End Sub

'-------------------------------------------------------------------------------
' 產出借貸不平的報表
'-------------------------------------------------------------------------------
Private Sub vExport_ShowDocumentBalanceReport()
    '建立必要報表
    reporter.GenerateEngagementOverview
    reporter.GenerateDocumentBalanceDetail
    '輸出報表
    exporter.ShowDocumentBalanceReport
End Sub

