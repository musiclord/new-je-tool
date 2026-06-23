VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewValidation 
   Caption         =   "Validation"
   ClientHeight    =   3015
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   4560
   OleObjectBlob   =   "ViewValidation.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewValidation"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    View
' Name:     Validation
' Purpose:  驗證結果使用者介面。
'           顯示 JET 測試的執行狀態與結果清單，
'           提供異常項目的檢視與匯出功能。
'===============================================================================

Public Event CheckCompleteness()
Public Event CheckDocumentBalance()
Public Event CheckINF()
Public Event CheckNullRecords()
Public Event Submitted()
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
