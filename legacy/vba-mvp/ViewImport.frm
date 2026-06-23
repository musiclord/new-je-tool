VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewImport 
   Caption         =   "Import"
   ClientHeight    =   3015
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   4560
   OleObjectBlob   =   "ViewImport.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewImport"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    View
' Name:     Import
' Purpose:  資料匯入使用者介面。
'           提供檔案選擇、欄位對應設定的表單，顯示匯入進度，
'           將操作事件委派給 PresenterImport 處理。
'===============================================================================

'--
Public Event ImportGeneralLedger(ByVal p_Format As String)
Public Event ImportTrialBalance(ByVal p_Format As String)
Public Event MapGeneralLedgerField()
Public Event MapTrialBalanceField()
Public Event UpdateCalendar()
Public Event Submitted()
Public Event OnClose()
'--
Private m_Format As String
'-- State Flags
Private m_IsGlReady As Boolean
Private m_IsTbReady As Boolean
Private m_IsCalendarReady As Boolean

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
