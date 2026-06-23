VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewProject 
   Caption         =   "Project"
   ClientHeight    =   2535
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   3750
   OleObjectBlob   =   "ViewProject.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewProject"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    View
' Name:     Project
' Purpose:  專案管理使用者介面。
'           提供新建/開啟專案的表單，顯示專案資訊，
'           將操作事件委派給 PresenterProject 處理。
'===============================================================================
Public Event CreateProjectRequested(ByVal p_ProjectName As String)
Public Event LoadProjectRequested(ByVal p_ProjectName As String)
Public Event OnClose()

Private Sub btnCreateProject_Click()
    RaiseEvent CreateProjectRequested(Trim$(CStr(Me.txtbProjectName.value)))
    Call UpdateProject '<--Refresh
End Sub

Private Sub btnLoadProject_Click()
    RaiseEvent LoadProjectRequested(Trim$(CStr(Me.lstbProjectName.value)))
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
Private Sub UpdateProject()
    Dim projects As New Collection
    Dim item As Variant
    Dim rootFolder As Folder
    Dim subFolder As Folder
    ' 設定工作目錄
    Set rootFolder = g_Fso.GetFolder(ThisWorkbook.Path)
    ' 收集子目錄
    For Each subFolder In rootFolder.SubFolders
        projects.Add subFolder.Name
    Next subFolder
    ' 更新專案目錄
    Me.lstbProjectName.Clear
    For Each item In projects
        Me.lstbProjectName.AddItem item
    Next item
End Sub
