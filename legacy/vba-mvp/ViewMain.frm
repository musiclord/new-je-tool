VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewMain 
   Caption         =   "Main"
   ClientHeight    =   2100
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   8175
   OleObjectBlob   =   "ViewMain.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewMain"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    View
' Name:     Main
' Purpose:  主控制台使用者介面。
'           顯示系統主選單與功能入口，接收使用者操作事件，
'           將事件委派給 PresenterMain 處理。
'===============================================================================
Public Event DataPreparation()
Public Event RunValidation()
Public Event ConfigureFilters()
Public Event ExportResults()
Public Event OnClose()

Private Sub btnDataPreparation_Click()
    ' Step - 1
    RaiseEvent DataPreparation
End Sub

Private Sub btnRunValidation_Click()
    ' Step - 2
    RaiseEvent RunValidation
End Sub

Private Sub btnConfigureFilters_Click()
    ' Step - 3
    RaiseEvent ConfigureFilters
End Sub

Private Sub btnExportResults_Click()
    ' Step -4
    RaiseEvent ExportResults
End Sub

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
