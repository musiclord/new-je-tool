VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "DbAccess"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Database
' Name:     Access
' Purpose:  Access 資料庫存取層 (DAO)。
'           封裝資料庫連線、交易管理 (Transaction)、
'           SQL 執行 (Query/NonQuery)，以及表結構檢查等底層操作。
'===============================================================================

Private Const CHUNK_SIZE As Long = 100000
Private Const EVENTS_INTERVAL As Double = 2#
Private m_Ws As DAO.Workspace
Private m_Db As DAO.Database
Private m_Conn As DAO.Connection
Private m_Logger As CoreLogger
Private m_Path As String
Private m_IsConnected As Boolean
Private m_InTransaction As Boolean

'===============================================================================
' Properties
'===============================================================================
Public Property Get Database() As DAO.Database
    Set Database = m_Db
End Property
Public Property Get Workspace() As DAO.Workspace
    Set Workspace = m_Ws
End Property
Public Property Get Path() As String
    Path = m_Path
End Property
Public Property Let Path(ByVal p_Value As String)
    m_Path = p_Value
End Property


'===============================================================================
' Methods
'===============================================================================

Public Sub Initialize(ByVal p_Path As String, ByVal p_Logger As CoreLogger)
    m_Path = p_Path
    Set m_Logger = p_Logger
    m_IsConnected = False
    m_InTransaction = False
End Sub

'===============================================================================

'===============================================================================
Public Sub Connect()
    ' 檢查資料庫連線
    ' 開啟資料庫
End Sub

Public Sub Disconnect()
    ' 斷開資料庫連線
End Sub

'===============================================================================

'===============================================================================
Public Sub BeginTrans()
    ' 開始交易
End Sub

Public Sub CommitTrans()
    ' 提交交易
End Sub

Public Sub RollbackTrans()
    ' 回滾交易
End Sub

'===============================================================================

'===============================================================================
Public Function ExecuteQuery(ByVal p_Sql As String) As DAO.Recordset
    ' 執行 SELECT 查詢並回傳 Recordset
End Function

Public Function ExecuteNonQuery( _
    ByVal p_Sql As String, _
    Optional ByVal p_Logging As Boolean = True _
) As Long
    ' 執行 INSERT, UPDATE, DELETE, CREATE TABLE 等非查詢 SQL
End Function

'===============================================================================

'===============================================================================
Public Function TableExists(ByVal p_TableName As String) As Boolean
    ' 檢查資料表是否存在
End Function

Public Function FieldExists( _
    ByVal p_TableName As String, _
    ByVal p_FieldName As String _
) As Boolean
    ' 檢查欄位是否存在於指定資料表
End Function

Public Function GetTableFields(ByVal p_TableName As String) As Collection
    ' 取得資料表的所有欄位名稱
End Function

Public Function DropTable(ByVal p_TableName As String) As Boolean
    ' 刪除資料表
End Function

'===============================================================================
' Helper
'===============================================================================
Public Sub Refresh(Optional ByVal p_ForceDeep As Boolean = False)
    ' 刷新資料庫快取
End Sub

Public Function CompactAndRepair() As Boolean
    ' 壓縮與修復資料庫
End Function

Public Function EscapeString(ByVal p_String As String) As String
    '將 SQL 字串中的單引號進行轉譯
    EscapeString = Replace(p_String, "'", "''")
End Function
