VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "CoreSqlBuilder"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Core
' Name:     CoreSqlBuilder
' Purpose:  SQL 語句建構器 (Fluent API)。
'           提供鏈式呼叫介面來組建 SELECT/JOIN/WHERE/GROUP BY 等
'           SQL 子句，避免手動字串拼接的錯誤風險。
'===============================================================================

'-- SELECT clause
Private m_SelectFields As String
Private m_SelectDistinct As Boolean

'-- FROM clause
Private m_FromTable As String
Private m_FromAlias As String
Private m_FromDbPath As String

'-- JOIN clauses
Private m_Joins As Collection

'-- WHERE clause
Private m_WhereConditions As String

'-- GROUP BY clause
Private m_GroupByFields As String

'-- HAVING clause
Private m_HavingCondition As String

'-- ORDER BY clause
Private m_OrderByFields As String

'-- INTO clause
Private m_IntoTable As String
Private m_IntoDbPath As String


'===============================================================================
' SELECT Clause
'===============================================================================
Public Function SelectFields(ParamArray fields() As Variant) As CoreSqlBuilder
    ' 選擇特定欄位
End Function

Public Function SelectDistinct() As CoreSqlBuilder
    ' 設定 DISTINCT
End Function

Public Function SelectAggregate( _
    ByVal aggFunc As String, _
    ByVal field As String, _
    Optional ByVal alias As String = "" _
) As CoreSqlBuilder
    ' 新增聚合函數欄位
    ' 範例: SelectAggregate("SUM", "[Amount]", "TotalAmount")
End Function


'===============================================================================
' FROM Clause
'===============================================================================
Public Function FromTable( _
    ByVal tableName As String, _
    Optional ByVal alias As String = "", _
    Optional ByVal dbPath As String = "" _
) As CoreSqlBuilder
    ' 設定來源資料表
End Function

'===============================================================================
' JOIN Clauses
'===============================================================================
Public Function InnerJoin( _
    ByVal tableName As String, _
    Optional ByVal alias As String = "", _
    Optional ByVal dbPath As String = "" _
) As CoreSqlBuilder
    ' 新增 INNER JOIN
End Function

Public Function LeftJoin( _
    ByVal tableName As String, _
    Optional ByVal alias As String = "", _
    Optional ByVal dbPath As String = "" _
) As CoreSqlBuilder
    ' 新增 LEFT JOIN
End Function

Public Function RightJoin( _
    ByVal tableName As String, _
    Optional ByVal alias As String = "", _
    Optional ByVal dbPath As String = "" _
) As CoreSqlBuilder
    ' 新增 RIGHT JOIN
End Function

Public Function OnCondition( _
    ByVal leftField As String, _
    ByVal operator As String, _
    ByVal rightField As String _
) As CoreSqlBuilder
    ' 設定最後一個 JOIN 的 ON 條件
End Function

'===============================================================================
' WHERE Clause
'===============================================================================
Public Function WhereCondition(ByVal condition As String) As CoreSqlBuilder
    ' 設定 WHERE 條件
End Function

Public Function AndWhere(ByVal condition As String) As CoreSqlBuilder
    ' 新增 AND 條件
End Function

Public Function OrWhere(ByVal condition As String) As CoreSqlBuilder
    ' 新增 OR 條件
End Function

'===============================================================================
' GROUP BY Clause
'===============================================================================
Public Function GroupByFields(ParamArray fields() As Variant) As CoreSqlBuilder
    ' 設定 GROUP BY 欄位
End Function

'===============================================================================
' HAVING Clause
'===============================================================================
Public Function HavingCondition(ByVal condition As String) As CoreSqlBuilder
    ' 設定 HAVING 條件
End Function

'===============================================================================
' ORDER BY Clause
'===============================================================================
Public Function OrderByFields(ParamArray fields() As Variant) As CoreSqlBuilder
    ' 設定 ORDER BY 欄位
End Function

'===============================================================================
' INTO Clause
'===============================================================================
Public Function IntoTable( _
    ByVal tableName As String, _
    Optional ByVal dbPath As String = "" _
) As CoreSqlBuilder
    ' 設定 INTO 目標表（用於 SELECT INTO）
End Function

'===============================================================================
' Build SQL
'===============================================================================

Public Function Build() As String
    ' 建立最終 SQL 字串
    Dim sql As String
    sql = ""
    '-- SELECT 子句
    '-- INTO 子句 (必須在 FROM 之前)
    '-- FROM 子句
    '-- JOIN 子句
    '-- WHERE 子句
    '-- GROUP BY 子句
    '-- HAVING 子句
    '-- ORDER BY 子句
    
    sql = sql & ";"
    Build = sql
End Function
