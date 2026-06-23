VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceAccountMapping"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Service
' Name:     AccountMapping
' Purpose:  會計科目對應服務。
'           管理原始科目代碼與標準化科目的對應關係，
'           支援科目重分類與統一化處理。
'
' Dependencies:
'     - ServiceValidation: 需先執行 CheckCompleteness() 產生 COMPLETENESS_DETAIL
'
' Workflow:
'     1. ExportTemplate()
'      ↓ 從 COMPLETENESS_DETAIL 匯出科目清單至 SHT_ACCOUNT_MAPPING
'     2. <<用戶手動標註>>
'      ↓ 用戶在 SHT_ACCOUNT_MAPPING 填寫 [Standardized Account Name]
'     3. ImportAccountMapping()
'      ↓ 讀取 SHT_ACCOUNT_MAPPING 資料並儲存至 DbSchema.TBL_ACCOUNT_MAPPING
'     4. ValidateAccountMapping()
'      ↓ 驗證映射關係
'
' Example:
'-------------------------------------------------------------------------------
'     GL_Number |   GL_Name            |  Standardized Account Name*
'     1101**    |   Not in TB          |  Cash
'     1102A*    |   [銀存]支票存款     |  Cash
'     1102C*    |   [銀存]外匯活期存款 |  Cash
'     1103**    |   定期存款           |  Cash
'-------------------------------------------------------------------------------
'===============================================================================

'--
Private m_Context As CoreContext
Private m_Db As DbAccess

'-------------------------------------------------------------------------------
'--
'-------------------------------------------------------------------------------
Public Sub Initialize(ByVal p_Context As CoreContext)
    Set m_Context = p_Context
    Set m_Db = p_Context.DbInput
End Sub

Public Function ExportTemplate(p_Worksheet As Worksheet) As Boolean
    ' 從 COMPLETENESS_DETAIL 匯出科目清單至 SHT_ACCOUNT_MAPPING
End Function

Public Function ImportAccountMapping(ByVal p_Recordset) As Boolean
    ' 讀取 SHT_ACCOUNT_MAPPING 資料並儲存至 DbSchema.TBL_ACCOUNT_MAPPING
End Function

Public Function ValidateAccountMapping() As Boolean
    ' 驗證映射關係
End Function
