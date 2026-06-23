Attribute VB_Name = "Util"
Option Explicit
'===============================================================================
' Module:   Util
' Purpose:
' Methods:
'===============================================================================
Private m_App As New ApplicationMain
'-- 通用暫存表
Public Const TBL_TEMP As String = "TEMP_DATA"

'-- 專案全域入口介面
'-------------------------------------------------------------------------------
Public Sub Launch()
    Call m_App.Initialize
    Call m_App.Run
End Sub

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

'-- 檢查資料方法
'-------------------------------------------------------------------------------
Public Function CheckDate(ByVal value As Variant) As Boolean
    ' Use: If Not CheckDate(date) Then Exit Sub
    ' CDate (Value)
End Function

Public Function CheckDouble(ByVal value As Variant) As Boolean
    ' Use: If Not CheckDouble(double) Then Exit Sub
    ' CDouble (Value)
End Function

Public Function CheckText(ByVal value As Variant) As Boolean
    ' Use If Not CheckText(text) Then Exit Sub
    ' CStr(Value)
End Function

