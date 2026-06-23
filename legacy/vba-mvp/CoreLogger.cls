VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "CoreLogger"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Core
' Name:     Logger
' Purpose:  統一日誌記錄服務。
'           提供 INFO/DEBUG/ERROR 等級的日誌輸出，
'           支援即時視窗 (Immediate Window) 與檔案輸出兩種模式。
'===============================================================================

'--
Private m_LogToFile As Boolean
Private m_LogFilePath As String


'--------------------------------------------------------------------------------
'
'--------------------------------------------------------------------------------
Public Sub Initialize( _
    Optional ByVal p_LogToFile As Boolean = False, _
    Optional ByVal p_LogFilePath As String = "" _
)
    ' 初始化
    m_LogToFile = p_LogToFile
    m_LogFilePath = p_LogFilePath
    If m_LogToFile And m_LogFilePath = "" Then
        m_LogFilePath = ThisWorkbook.Path & "\jet_vba.log"
    End If
End Sub

Public Sub LogInfo(ByVal message As String)
    WriteLog "INFO", message
End Sub

Public Sub LogError(ByVal message As String, Optional ByVal errNumber As Long = 0)
    Dim msg As String
    msg = message
    If errNumber <> 0 Then
        msg = msg & " (Err: " & Err.Number & ")"
    End If
    WriteLog "ERROR", msg
End Sub

Public Sub LogDebug(ByVal message As String)
    WriteLog "DEBUG", message
End Sub

'--------------------------------------------------------------------------------
'
'--------------------------------------------------------------------------------
Private Sub WriteLog(ByVal level As String, ByVal message As String)
    Dim timestamp As String
    timestamp = Format(Now, "yyyy-mm-dd hh:mm:ss")
    
    Dim logLine As String
    logLine = "[" & timestamp & "] [" & level & "] " & message
    
    ' 輸出至即時運算視窗
    Debug.Print logLine
    
    ' 若設置要寫入檔案則執行
    If m_LogToFile Then
        On Error Resume Next ' 避免程序崩潰
        Dim fso As Object
        Set fso = CreateObject("Scripting.FileSystemObject")
        Dim ts As Object
        Set ts = fso.OpenTextFile(m_LogFilePath, 8, True) ' 8 = ForAppending
        ts.WriteLine logLine
        ts.Close
        Set ts = Nothing
        Set fso = Nothing
        On Error GoTo 0
    End If
End Sub
