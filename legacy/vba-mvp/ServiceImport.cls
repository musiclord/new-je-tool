VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceImport"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Service
' Name:     Import
' Purpose:  資料匯入服務。
'           處理外部檔案 (CSV/Excel) 的解析與轉換，
'           將總帳 (GL)、試算表 (TB) 等資料寫入 Access 資料庫。
'===============================================================================

'--
Private m_Context As CoreContext
Private m_Db As DbAccess

Public Sub Initialize(ByVal p_Context As CoreContext)
    Set m_Context = p_Context
    Set m_Db = p_Context.DbInput
End Sub


Public Function ImportGl()
    '...
End Function

Public Function ImportTb()
    '...
End Function
