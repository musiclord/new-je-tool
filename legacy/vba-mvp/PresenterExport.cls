VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterExport"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Name:     Export
' Purpose:  資料匯出展示器
'===============================================================================

'--
Private WithEvents m_View As ViewExport
Attribute m_View.VB_VarHelpID = -1
Private m_Context As CoreContext

Public Sub Initialize(ByVal p_Context As CoreContext)
    Set m_Context = p_Context
End Sub

Private Sub m_View_ExportWorkingPaper()
    '...
End Sub

Private Sub m_View_ShowEngagementOverview()
    '...
End Sub

Private Sub m_View_ShowValidationReport()
    '...
End Sub

Private Sub m_View_ShowCompletenessReport()
    '...
End Sub

Private Sub m_View_ShowDocumentBalanceReport()
    '...
End Sub

Private Sub m_View_OnClose()
    m_View.Hide
    Unload m_View
End Sub
