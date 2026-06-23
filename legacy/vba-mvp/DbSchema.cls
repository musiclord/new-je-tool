VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "DbSchema"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Database
' Name:     Schema
' Purpose:  資料庫結構定義與管理。
'           借鑑於 Kimball 的 ETL 架構，分為:
'           DbSchema (Facade - 統一入口)
'               ├── 1. Source    ← JE/TB 動態映射
'               ├── 2. Staging   ← 中間表名稱
'               └── 3. Target    ← 報表輸出定義
'
'===============================================================================

'--
Private m_Source As DbSchemaSource
Private m_Staging As DbSchemaStaging
Private m_Target As DbSchemaTarget

'-------------------------------------------------------------------------------
'-- 初始化
'-------------------------------------------------------------------------------
Public Sub Initialize( _
    ByVal p_GL As MapperFieldGl, _
    ByVal p_TB As MapperFieldTb _
)
    Set m_Source = New DbSchemaSource
    Set m_Staging = New DbSchemaStaging
    Set m_Target = New DbSchemaTarget
    '--
    m_Source.Initialize p_GL, p_TB
    m_Staging.Initialize
    m_Target.Initialize
End Sub


Public Property Get Source() As DbSchemaSource
    ' Layer 1: Source Layer - 核心業務表的動態欄位映射
    Set Source = m_Source
End Property

Public Property Get Staging() As DbSchemaStaging
    ' Layer 2: ETL Layer - 轉換過程中的中間表
    Set Staging = m_Staging
End Property

Public Property Get Target() As DbSchemaTarget
    ' Layer 3: Target Layer - 最終輸出的報表
    Set Target = m_Target
End Property
