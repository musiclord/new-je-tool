VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewMain 
   Caption         =   "Main"
   ClientHeight    =   5385
   ClientLeft      =   105
   ClientTop       =   405
   ClientWidth     =   9585.001
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
' Form:     ViewMain
' Purpose:
' Methods:
'===============================================================================
Public Event DoStep1()
Public Event DoStep2()
Public Event DoStep3()
Public Event DoStep4()
Public Event ExitApplication()

Public Sub Initialize(ByVal title As String)
    Me.Caption = title
End Sub

Private Sub btnDoStep1_Click()
    '觸發步驟1 - 匯入資料
    RaiseEvent DoStep1
End Sub

Private Sub btnDoStep2_Click()
    '觸發步驟2 - 驗證資料
    RaiseEvent DoStep2
End Sub

Private Sub btnDoStep3_Click()
    '觸發步驟3 - 篩選資料
    RaiseEvent DoStep3
End Sub

Private Sub btnDoStep4_Click()
    '觸發步驟4 - 匯出資料
    RaiseEvent DoStep4
End Sub

Private Sub btnExit_Click()
    Me.Hide
    Unload Me
    RaiseEvent ExitApplication
End Sub
