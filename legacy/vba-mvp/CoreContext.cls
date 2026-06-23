VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "CoreContext"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Core
' Name:     Context
' Purpose:  應用程式上下文容器 (IoC Container)。
'           集中管理核心依賴項 (Logger、DbAccess、DbSchema)，
'           並持有專案組態資訊 (專案名稱、客戶、會計期間等)。
'
'
'┌─────────────────────────────────────────────────────────────────────────────┐
'│                              應用程式流程                                   │
'├─────────────────────────────────────────────────────────────────────────────┤
'│                                                                             │
'│  1. App.Bootstrap()                                                         │
'│     └── CoreContext.Initialize()                                            │
'│         ├── Logger.Initialize()        <立即>                               │
'│         ├── Schema.Initialize()        <立即>                               │
'│         └── DbAccess = Nothing         <延遲>                               │
'│                                                                             │
'│  2. CoreProject.Initialize(Context)                                         │
'│     └── Set m_Context = p_Context       <依賴注入>                          │
'│                                                                             │
'│  3. CoreProject.CreateProject() / LoadProject()                             │
'│     ├── 建立/驗證路徑                                                       │
'│     └── m_Context.InitializeDatabase(path)   <觸發 DbAccess 初始化>         │
'│         └── DbAccess.Initialize(path, Logger)                               │
'│         └── DbAccess.Connect()                                              │
'│                                                                             │
'│  4. Service 使用                                                            │
'│     └── m_Context.DbAccess.ExecuteQuery(sql)   <可正常使用>                 │
'│                                                                             │
'└─────────────────────────────────────────────────────────────────────────────┘
'===============================================================================

'-- Core Dependencies
Private m_Project As CoreProject
Private m_Logger As CoreLogger
Private m_Schema As DbSchema
'--
Private m_DbInput As DbAccess
Private m_DbValid As DbAccess
Private m_DbCriteria As DbAccess
Private m_DbReport As DbAccess

'===============================================================================
' Initialization
'===============================================================================
Public Sub Initialize()
    ' 初始化核心物件
    Dim mapperGl As New MapperFieldGl
    Dim mapperTb As New MapperFieldTb
    Set m_Schema = New DbSchema
    Set m_Logger = New CoreLogger
    '--
    mapperGl.Initialize
    mapperTb.Initialize
    m_Schema.Initialize mapperGl, mapperTb
    m_Logger.Initialize False, ""
End Sub

Public Sub InitializeDatabase(ByVal p_ProjectPath As String)
    Set m_DbInput = New DbAccess
    m_DbInput.Initialize p_ProjectPath & "\input.accdb", m_Logger
    
    Set m_DbValid = New DbAccess
    m_DbValid.Initialize p_ProjectPath & "\valid.accdb", m_Logger
    
    Set m_DbCriteria = New DbAccess
    m_DbCriteria.Initialize p_ProjectPath & "\criteria.accdb", m_Logger
    
    Set m_DbReport = New DbAccess
    m_DbReport.Initialize p_ProjectPath & "\report.accdb", m_Logger
End Sub
'===============================================================================
' Properties - Core Dependencies
'===============================================================================
Public Property Get Logger() As CoreLogger
    Set Logger = m_Logger
End Property

Public Property Get DbInput() As DbAccess
    Set DbInput = m_DbInput
End Property

Public Property Get DbValid() As DbAccess
    Set DbValid = m_DbValid
End Property

Public Property Get DbCriteria() As DbAccess
    Set DbCriteria = m_DbCriteria
End Property

Public Property Get DbReport() As DbAccess
    Set DbReport = m_DbReport
End Property

Public Property Get Schema() As DbSchema
    Set Schema = m_Schema
End Property

Public Property Get Project() As CoreProject
    ' Project = m_Project
End Property
