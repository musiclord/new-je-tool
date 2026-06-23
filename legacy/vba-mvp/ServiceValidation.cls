VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceValidation"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Service
' Name:     Validation
' Purpose:  資料驗證服務。
'           執行 JET 測試規則，包含借貸平衡檢查、完整性測試、
'           日期範圍驗證等，並回傳驗證結果。
'===============================================================================

'--
Private m_Context As CoreContext
Private m_Db As DbAccess

Public Sub Initialize(ByVal p_Context As CoreContext)
    Set m_Context = p_Context
    Set m_Db = p_Context.DbValid
End Sub

