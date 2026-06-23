VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceFilter"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Service
' Name:     Filter
' Purpose:  資料篩選服務。
'           根據使用者定義的條件 (金額區間、科目範圍、日期等)
'           篩選日記帳分錄，產生待測試的抽樣母體。
'===============================================================================

'--
Private m_Context As CoreContext
Private m_Db As DbAccess

Public Sub Initialize(ByVal p_Context As CoreContext)
    Set m_Context = p_Context
    Set m_Db = p_Context.DbCriteria
End Sub
