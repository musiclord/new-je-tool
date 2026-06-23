Attribute VB_Name = "AppUtils"
Option Explicit
'===============================================================================
' Layer:    Global
' Name:     Utils
' Purpose:  通用工具函式庫。
'           提供檔案系統操作 (FSO)、字串處理、日期格式化等
'           跨模組共用的輔助功能。
'===============================================================================

'--
Public g_Fso As Scripting.FileSystemObject

'--------------------------------------------------------------------------------
' Initialization
'--------------------------------------------------------------------------------
Private Sub Class_Initialize()
    Set g_Fso = New Scripting.FileSystemObject
End Sub



'-------------------------------------------------------------------------------
'-- 查詢輔助語法
'-------------------------------------------------------------------------------
Public Function Nz( _
    ByVal fieldName As String, _
    Optional ByVal defaultValue As String = "0" _
) As String
    ' 將欄位名稱轉換成 IIF(ISNULL(...),defaultValue,...) SQL 語法
    fieldName = Trim$(fieldName)
    ' 如果 fieldName 包含空格或特殊字元，用方括號包圍
    fieldName = "[" & fieldName & "]"
    Nz = "IIF(ISNULL(" & fieldName & ")," & defaultValue & "," & fieldName & ")"
End Function

Public Function SanitizeNumericField(ByVal fieldName As String) As String
    ' 轉換空值或任何非值為零
    SanitizeNumericField = _
        "CDbl(IIf(" & vbCrLf & _
        "    [" & fieldName & "] IS NULL " & vbCrLf & _
        "        OR Trim([" & fieldName & "]) = '' " & vbCrLf & _
        "        OR Trim([" & fieldName & "]) = '-', " & vbCrLf & _
        "    0, [" & fieldName & "]))"
End Function

