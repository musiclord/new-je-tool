VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceDateDimension"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Service
' Name:     DateDimension
' Purpose:  日期維度表管理服務。
'           建立與維護日期維度資料 (含假日、補班日標記)，
'           支援依日期進行資料分析與篩選。
'===============================================================================

'--
Private m_Context As CoreContext
Private m_Db As DbAccess

'-------------------------------------------------------------------------------
'--
'-------------------------------------------------------------------------------
Public Sub Initialize(ByVal p_Context As CoreContext)
    Set m_Context = p_Context
    Set m_Db = p_Context.DbInput
End Sub

Public Sub MergeWeekends(ByVal p_SelectedDays As Variant)
    ' 將用戶選定的周末日期，合併至 DateDimension 表
End Sub

Public Sub MergeHolidays(BrsHolidays As DAO.Recordset)
    ' 將用戶輸入的假期日期，合併至 DateDimension 表
End Sub

Public Sub MergeMakeupdays(rsMakeupdays As DAO.Recordset)
    ' 將用戶輸入的補班日期，合併至 DateDimension 表
End Sub

Public Sub CreateDateDimensionTable()
    ' 設定日期維度表的起始日及結束日
    Dim dateStart As Date, dateEnd As Date
    
End Sub

