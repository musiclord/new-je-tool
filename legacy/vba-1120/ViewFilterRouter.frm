VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewFilterRouter 
   Caption         =   "Router (Temp)"
   ClientHeight    =   2685
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   2655
   OleObjectBlob   =   "ViewFilterRouter.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewFilterRouter"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' APPLICATION ROUTER ARCHIVE
' ROUTES TO DIFFERENT FILTER VERSIONS
' Description:
'   - 提供 Legacy 與 Current 版本之間的切換介面
'   - 本表單僅負責路由決策,不包含任何業務邏輯
'   - 透過事件機制將使用者選擇傳遞給 ApplicationMain 控制器
'===============================================================================
Public Event RouteToCurrent()
Public Event RouteToLegacy()

Private Sub btnCurrent_Click()
    '---------------------------------------------------------------------------
    ' 切換至當前篩選條件邏輯
    '---------------------------------------------------------------------------
    Me.Hide
    RaiseEvent RouteToCurrent
End Sub

Private Sub btnLegacy_Click()
    '---------------------------------------------------------------------------
    ' 切換至傳統篩選條件邏輯
    '---------------------------------------------------------------------------
    Me.Hide
    RaiseEvent RouteToLegacy
End Sub

Private Sub btnExit_Click()
    '---------------------------------------------------------------------------
    ' 退出暫存路由視窗
    '---------------------------------------------------------------------------
    Me.Hide
End Sub
