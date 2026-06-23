VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewExport 
   Caption         =   "Export"
   ClientHeight    =   3015
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   4560
   OleObjectBlob   =   "ViewExport.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewExport"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    View
' Name:     Export
' Purpose:  匯出工作底稿及記錄文件
'===============================================================================

Public Event ExportWorkingPaper()
Public Event ShowEngagementOverview()
Public Event ShowValidationReport()
Public Event ShowCompletenessReport()
Public Event ShowDocumentBalanceReport()
Public Event OnClose()

Private Sub btnClose_Click()
    RaiseEvent OnClose
End Sub

Private Sub UserForm_QueryClose(Cancel As Integer, CloseMode As Integer)
    If CloseMode = vbFormControlMenu Then   ' 使用者點了 X 按鈕
        Cancel = True       ' 阻止預設關閉行為
        RaiseEvent OnClose  ' 交給 Presenter 關閉介面
    End If
End Sub

'-------------------------------------------------------------------------------
' Helper
'-------------------------------------------------------------------------------
