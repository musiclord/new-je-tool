VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterImport"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Name:     Import
' Purpose:  資料匯入展示器。
'           處理檔案選擇、欄位對應設定等 UI 事件，
'           呼叫 ServiceImport 執行匯入並更新進度顯示。
'===============================================================================

'--
Private WithEvents m_View As ViewImport
Private m_Context As CoreContext


Public Sub Initialize(ByVal p_Context As CoreContext)
    ' 初始化核心物件
    Set m_Context = p_Context
End Sub

Private Sub m_View_ImportGeneralLedger(ByVal p_Format As String)
    '...
End Sub

Private Sub m_View_ImportTrialBalance(ByVal p_Format As String)
    '...
End Sub

Private Sub m_View_MapGeneralLedgerField()
    '...
End Sub

Private Sub m_View_MapTrialBalanceField()
    '...
End Sub

Private Sub m_View_UpdateCalendar()
    '...
End Sub

Private Sub m_View_Submitted()
    '...
End Sub

Private Sub m_View_OnClose()
    m_View.Hide
    Unload m_View
End Sub
