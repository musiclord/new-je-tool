VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterFilter"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Name:     Filter
' Purpose:  篩選條件展示器。
'           處理篩選條件的 UI 設定與驗證，
'           呼叫 ServiceFilter 執行篩選並回傳結果至 View。
'===============================================================================

'--
Private WithEvents m_View As ViewFilter
Attribute m_View.VB_VarHelpID = -1
Private m_Context As CoreContext


Public Sub Initialize(ByVal p_Context As CoreContext)
    ' 初始化核心物件
    Set m_Context = p_Context
End Sub

Private Sub m_View_RemoveCriteria()
    '...
End Sub

Private Sub m_View_ShowAccountMapping()
    '...
End Sub

Private Sub m_View_ImportAccountMapping()
    '...
End Sub

Private Sub m_View_AddCriteria()
    '...
End Sub

Private Sub m_View_AddCriterion()
    '...
End Sub

Private Sub m_View_AddSql()
    '...
End Sub

Private Sub m_View_ExecuteCriteria()
    '...
End Sub

Private Sub m_View_RemoveCriterion()
    '...
End Sub

Private Sub m_View_Submitted()
    '...
End Sub

Private Sub m_View_OnClose()
    m_View.Hide
    Unload m_View
End Sub
