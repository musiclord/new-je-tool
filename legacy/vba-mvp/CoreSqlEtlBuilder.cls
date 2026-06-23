VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "CoreSqlEtlBuilder"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Core
' Name:     SqlEtlBuilder
' Purpose:  ETL 操作 SQL 產生器。
'           模擬 IDEA 的資料操作功能 (JoinDatabase、AppendDatabase、
'           Summarization、Extraction)，產生對應的 Access SQL 語句。
'===============================================================================

'-------------------------------------------------------------------------------
' 模擬 IDEA JoinDatabase 操作
'
' 參數:
'   primaryTable: 主要資料表
'   secondaryTable: 次要資料表
'   matchFields: 配對欄位 Array(Array("PrimaryField", "SecondaryField"))
'   outputTable: 輸出表名稱
'   joinType: "INNER", "LEFT", "RIGHT", "FULL"
'   includePrimaryFields: 是否包含主表所有欄位
'   secondaryFields: 次表要包含的欄位 Array("Field1", "Field2")
'   primaryDb: 主表資料庫路徑
'   secondaryDb: 次表資料庫路徑
'   outputDb: 輸出資料庫路徑
'-------------------------------------------------------------------------------
Public Function JoinTables( _
    ByVal primaryTable As String, _
    ByVal secondaryTable As String, _
    ByVal matchFields As Variant, _
    Optional ByVal outputTable As String = "", _
    Optional ByVal joinType As String = "LEFT", _
    Optional ByVal includePrimaryFields As Boolean = True, _
    Optional ByVal secondaryFields As Variant = Empty, _
    Optional ByVal primaryDb As String = "", _
    Optional ByVal secondaryDb As String = "", _
    Optional ByVal outputDb As String = "" _
) As String
    '...
End Function

'-------------------------------------------------------------------------------
' 模擬 IDEA AppendDatabase 操作
'
' 參數:
'   outputTable: 輸出表名稱
'   outputDb: 輸出資料庫路徑
'   tables: 要附加的資料表 Array(Array("Table1", "db1.accdb"), Array("Table2", ""))
'-------------------------------------------------------------------------------
Public Function AppendTables( _
    ByVal outputTable As String, _
    Optional ByVal outputDb As String = "" _
) As String
    '...
End Function

'-------------------------------------------------------------------------------
' 模擬 IDEA Summarization 操作
'
' 參數:
'   sourceTable: 來源資料表
'   groupFields: GROUP BY 欄位 Array("Field1", "Field2")
'   aggregateFields: 聚合欄位 Array(Array("SUM", "Amount", "TotalAmount"))
'   outputTable: 輸出表名稱
'   where Condition: WHERE 條件
'   sourceDb: 來源資料庫路徑
'   outputDb: 輸出資料庫路徑
'-------------------------------------------------------------------------------
Public Function Summarization( _
    ByVal sourceTable As String, _
    ByVal groupFields As Variant, _
    ByVal aggregateFields As Variant, _
    Optional ByVal outputTable As String = "", _
    Optional ByVal WhereCondition As String = "", _
    Optional ByVal sourceDb As String = "", _
    Optional ByVal outputDb As String = "" _
) As String
    '...
End Function

'-------------------------------------------------------------------------------
' 模擬 IDEA Extraction 操作
'
' 參數:
'   sourceTable: 來源資料表
'   whereCondition: WHERE 條件
'   outputTable: 輸出表名稱
'   fields: 要擷取的欄位 Array("Field1", "Field2")，空則全選
'   sourceDb: 來源資料庫路徑
'   outputDb: 輸出資料庫路徑
'-------------------------------------------------------------------------------
Public Function ExtractWhere( _
    ByVal sourceTable As String, _
    ByVal WhereCondition As String, _
    Optional ByVal outputTable As String = "", _
    Optional ByVal fields As Variant = Empty, _
    Optional ByVal sourceDb As String = "", _
    Optional ByVal outputDb As String = "" _
) As String
    '...
End Function

'-------------------------------------------------------------------------------
' Access 不支援 FULL OUTER JOIN，使用 LEFT + RIGHT UNION 模擬
'-------------------------------------------------------------------------------
Public Function FullOuterJoin( _
    ByVal primaryTable As String, _
    ByVal secondaryTable As String, _
    ByVal matchFields As Variant, _
    Optional ByVal outputTable As String = "", _
    Optional ByVal primaryDb As String = "", _
    Optional ByVal secondaryDb As String = "", _
    Optional ByVal outputDb As String = "" _
) As String
    '...
End Function

