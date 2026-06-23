VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "PresenterValidation"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Presenter
' Name:     Validation
' Purpose:  資料驗證展示器。
'
'===============================================================================

'--
Private WithEvents m_View As ViewValidation
Attribute m_View.VB_VarHelpID = -1
Private m_Context As CoreContext

Public Sub Initialize(ByVal p_Context As CoreContext)
    Set m_Context = p_Context
End Sub

Private Sub m_View_CheckCompleteness()
    '...
End Sub

Private Sub m_View_CheckDocumentBalance()
    '...
End Sub

Private Sub m_View_CheckINF()
    '...
End Sub

Private Sub m_View_CheckNullRecords()
    '...
End Sub

Private Sub m_View_Submitted()
    '...
End Sub

Private Sub m_View_OnClose()
    m_View.Hide
    Unload m_View
End Sub
