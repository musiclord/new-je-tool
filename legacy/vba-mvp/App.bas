Attribute VB_Name = "App"
Option Explicit
'===============================================================================
' Layer:    Global
' Name:     App
' Purpose:  應用程式進入點與全域上下文管理。
'           提供 Main() 進入點、Bootstrap 初始化流程，
'           以及全域 CoreContext 實例的生命週期控制。




'===============================================================================

'-- 全域 Context
Public g_Context As CoreContext
'-- 私有 Presenter（保持參考避免被釋放）
Private m_PresenterProject As PresenterProject
Private m_PresenterMain As PresenterMain
Private m_PresenterImport As PresenterImport
Private m_PresenterValidation As PresenterValidation
Private m_PresenterFilter As PresenterFilter
Private m_PresenterExport As PresenterExport

'===============================================================================
' Entry Point
'===============================================================================
Public Sub Main()
    ' Entry point for the application
    Call Bootstrap
    Call ShowProjectView
End Sub

Public Sub Bootstrap()
    If g_Context Is Nothing Then
        Set g_Context = New CoreContext
        g_Context.Initialize
    End If
End Sub

Public Sub ResetApp()
    Set m_PresenterProject = Nothing
    Set m_PresenterMain = Nothing
    Set m_PresenterImport = Nothing
    Set m_PresenterFilter = Nothing
    Set m_PresenterExport = Nothing
    Set g_Context = Nothing
End Sub

'===============================================================================
' View Router（僅負責組裝，不含業務邏輯）
'===============================================================================
Public Sub ShowProjectView()
    Set m_PresenterProject = New PresenterProject
    m_PresenterProject.Initialize g_Context
    ViewProject.Show vbModeless
End Sub

Public Sub ShowMainView()
    Set m_PresenterMain = New PresenterMain
    m_PresenterMain.Initialize g_Context
    ViewMain.Show vbModeless
End Sub

Public Sub ShowImportView()
    Set m_PresenterImport = New PresenterImport
    m_PresenterImport.Initialize g_Context
    ViewImport.Show vbModeless
End Sub

Public Sub ShowValidationView()
    Set m_PresenterValidation = New PresenterValidation
    m_PresenterValidation.Initialize g_Context
    ViewValidation.Show vbModeless
End Sub

Public Sub ShowFilterView()
    Set m_PresenterFilter = New PresenterFilter
    m_PresenterFilter.Initialize g_Context
    ViewFilter.Show vbModeless
End Sub

Public Sub ShowExportView()
    Set m_PresenterExport = New PresenterExport
    m_PresenterExport.Initialize g_Context
    ViewExport.Show vbModeless
End Sub
