VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceFieldMapping"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Service
' Name:     FieldMapping
' Purpose:  欄位對應服務。
'           處理匯入資料的欄位名稱與系統標準欄位的對應，
'           確保不同來源資料可正確解析與儲存。
'===============================================================================

'--
Private m_Context As CoreContext
Private m_Db As DbAccess

Public Sub Initialize(ByVal p_Context As CoreContext)
    Set m_Context = p_Context
    Set m_Db = p_Context.DbInput
End Sub

Public Sub LoadMapping()

End Sub

Public Sub SaveMapping()

End Sub

Public Function AutoMatch() As Boolean

End Function
