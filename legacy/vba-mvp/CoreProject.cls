VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "CoreProject"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Core
' Name:     Project
' Purpose:  專案生命週期管理器。
'           負責建立新專案 (含資料庫初始化)、載入既有專案，
'           以及專案資料夾結構的維護。
'===============================================================================

'--
Private m_Context As CoreContext
Private m_Path As String
'-- 專案資訊
Private m_CompanyName As String     '--公司名稱
Private m_PeriodStart As Date       '--測試資料期間開始日
Private m_PeriodEnd As Date         '--測試資料期間結束日
Private m_PrepStartDate As Date     '--財報準備期間開始日
Private m_ProjectName As String
Private m_ClientName As String
'--使用者資訊
Private m_WSHnet As WshNetwork
Private m_UserName As String        '--使用者名稱
Private m_UserDomain As String
Private m_UserFullName As String    '--使用者全名
Private m_UserObject As Object

'-------------------------------------------------------------------------------
'-- 初始化
'-------------------------------------------------------------------------------
Public Sub Initialize(ByVal p_Context As CoreContext)
    Set m_Context = p_Context
End Sub

Public Function CreateProject(ByVal p_ProjectName As String) As Boolean
    ' 依路徑創建專案
    CreateProject = False
    m_Path = ThisWorkbook.Path & "\" & p_ProjectName
    If Dir(m_Path, vbDirectory) <> "" Then
        MsgBox "專案已存在", vbExclamation, "創建專案"
        Exit Function
    Else
        MkDir m_Path
        CreateDatabase "input.accdb"
        CreateDatabase "valid.accdb"
        CreateDatabase "criteria.accdb"
        CreateDatabase "report.accdb"
        m_Context.InitializeDatabase m_Path
    End If
    CreateProject = True
End Function

Public Function LoadProject(ByVal p_ProjectName As String) As Boolean
    ' 依路徑載入專案
    LoadProject = False
    m_Path = ThisWorkbook.Path & "\" & p_ProjectName
    If Dir(m_Path, vbDirectory) = "" Then
        MsgBox "專案不存在", vbExclamation, "載入專案"
        Exit Function
    Else
        LoadDatabase "input.accdb"
        LoadDatabase "valid.accdb"
        LoadDatabase "criteria.accdb"
        LoadDatabase "report.accdb"
        m_Context.InitializeDatabase m_Path
    LoadProject = True
    End If
End Function

'===============================================================================
' Helper
'===============================================================================
Private Function CreateDatabase(ByVal p_Name As String) As DbAccess
    ' 根據專案目錄創建資料庫檔案
    Dim dbe As New DAO.DBEngine
    Dim dbs As DAO.Database
    Dim db As New DbAccess
    Dim dbPath As String
    '--
    dbPath = m_Path & "\" & p_Name
    Set dbs = dbe.CreateDatabase(dbPath, dbLangGeneral)
    dbs.Close
    db.Path = dbPath
    Set CreateDatabase = db
End Function

Private Function LoadDatabase(ByVal p_Name As String) As DbAccess
    ' 根據專案目錄載入資料庫檔案
    Dim db As New DbAccess
    Dim dbPath As String
    '--
    dbPath = m_Path & "\" & p_Name
    db.Path = dbPath
    db.Connect '<-- test valid
    Set LoadDatabase = db
End Function



