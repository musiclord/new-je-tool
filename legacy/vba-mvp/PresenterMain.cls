VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterMain"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Name:     Main
' Purpose:  主畫面展示器。
'           協調主控制台的使用者操作，包含資料準備、驗證執行、
'           篩選設定、結果匯出等主要流程的事件處理。
'===============================================================================

'--
Private WithEvents m_View As ViewMain
Attribute m_View.VB_VarHelpID = -1
Private m_Context As CoreContext

Public Sub Initialize(ByVal p_Context As CoreContext)
    ' 初始化核心物件
    Set m_Context = p_Context
End Sub

Private Sub m_View_DataPreparation()
    App.ShowImportView
End Sub

Private Sub m_View_RunValidation()
    App.ShowValidationView
End Sub

Private Sub m_View_ConfigureFilters()
    App.ShowFilterView
End Sub

Private Sub m_View_ExportResults()
    '...
End Sub

Private Sub m_View_OnClose()
    m_View.Hide
    Unload m_View
End Sub
