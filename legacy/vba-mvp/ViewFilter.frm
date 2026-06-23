VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewFilter 
   Caption         =   "Filter"
   ClientHeight    =   3015
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   4560
   OleObjectBlob   =   "ViewFilter.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewFilter"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    View
' Name:     Filter
' Purpose:  篩選條件使用者介面。
'           提供篩選條件輸入表單 (金額、日期、科目等)，
'           將操作事件委派給 PresenterFilter 處理。
'===============================================================================

Public Event ShowAccountMapping()
Public Event ImportAccountMapping()
Public Event AddSql()
Public Event AddCriteria()
Public Event AddCriterion()
Public Event RemoveCriteria()
Public Event RemoveCriterion()
Public Event ExecuteCriteria()
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
