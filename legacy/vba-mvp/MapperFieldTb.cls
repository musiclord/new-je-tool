VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "MapperFieldTb"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Mapper
' Name:     FieldTb
' Purpose:  動態管理 TB (Trial Balance) 資料表的欄位映射
'===============================================================================

'--
Private m_Name As String
Private m_Method As Long
Private m_Fields As New Dictionary

'-------------------------------------------------------------------------------
'--
'-------------------------------------------------------------------------------
Public Sub Initialize()
    Dim fields As Variant
    Dim i As Long
    '--
    m_Name = "TB"
    ' 設置初始化欄位陣列
    fields = Array( _
        "FLD_CHANGE_AMOUNT", _
        "FLD_OPENING_BALANCE", _
        "FLD_OPENING_DEBIT_BALANCE", _
        "FLD_OPENING_CREDIT_BALANCE", _
        "FLD_CLOSING_BALANCE", _
        "FLD_CLOSING_DEBIT_BALANCE", _
        "FLD_CLOSING_CREDIT_BALANCE", _
        "FLD_DEBIT_AMOUNT", _
        "FLD_CREDIT_AMOUNT", _
        "FLD_ACCOUNT_CODE", _
        "FLD_ACCOUNT_NAME", _
        "K_AMOUNT" _
    )
    ' 將陣列初始化給欄位字典
    For i = LBound(fields) To UBound(fields)
        m_Fields(fields(i)) = fields(i)
    Next i
End Sub

Public Sub SetFields()
    ' 設定指定欄位鍵值對
End Sub

Public Sub LogFields()
    ' 記錄欄位狀態至日誌
End Sub

'-------------------------------------------------------------------------------
'-- Property
'-------------------------------------------------------------------------------
Public Property Get Name() As String
    Name = m_Name
End Property
Public Property Let Name(ByVal p_Value As String)
    m_Name = p_Value
End Property

Public Property Get Method() As Long
    Method = m_Method
End Property
Public Property Let Method(ByVal p_Value As Long)
    m_Method = p_Value
End Property

Public Property Get fields() As Dictionary
    Set fields = m_Fields
End Property

'-------------------------------------------------------------------------------
'-- Intellisense
'-------------------------------------------------------------------------------
Public Property Get FLD_CHANGE_AMOUNT() As String
    ' [欄位] 變動金額
    FLD_CHANGE_AMOUNT = m_Fields("FLD_CHANGE_AMOUNT")
End Property
Public Property Get FLD_OPENING_BALANCE() As String
    ' [欄位] 期初金額
    FLD_OPENING_BALANCE = m_Fields("FLD_OPENING_BALANCE")
End Property
Public Property Get FLD_OPENING_DEBIT_BALANCE() As String
    ' [欄位] 期初借方金額
    FLD_OPENING_DEBIT_BALANCE = m_Fields("FLD_OPENING_DEBIT_BALANCE")
End Property
Public Property Get FLD_OPENING_CREDIT_BALANCE() As String
    ' [欄位] 期初貸方金額
    FLD_OPENING_CREDIT_BALANCE = m_Fields("FLD_OPENING_CREDIT_BALANCE")
End Property
Public Property Get FLD_CLOSING_BALANCE() As String
    ' [欄位] 期末金額
    FLD_CLOSING_BALANCE = m_Fields("FLD_CLOSING_BALANCE")
End Property
Public Property Get FLD_CLOSING_DEBIT_BALANCE() As String
    ' [欄位] 期末借方金額
    FLD_CLOSING_DEBIT_BALANCE = m_Fields("FLD_CLOSING_DEBIT_BALANCE")
End Property
Public Property Get FLD_CLOSING_CREDIT_BALANCE() As String
    ' [欄位] 期末貸方金額
    FLD_CLOSING_CREDIT_BALANCE = m_Fields("FLD_CLOSING_CREDIT_BALANCE")
End Property
Public Property Get FLD_DEBIT_AMOUNT() As String
    ' [欄位] 借方金額
    FLD_DEBIT_AMOUNT = m_Fields("FLD_DEBIT_AMOUNT")
End Property
Public Property Get FLD_CREDIT_AMOUNT() As String
    ' [欄位] 貸方金額
    FLD_CREDIT_AMOUNT = m_Fields("FLD_CREDIT_AMOUNT")
End Property
Public Property Get FLD_ACCOUNT_CODE() As String
    ' [欄位] 科目代號
    FLD_ACCOUNT_CODE = m_Fields("FLD_ACCOUNT_CODE")
End Property
Public Property Get FLD_ACCOUNT_NAME() As String
    ' [欄位] 科目名稱
    FLD_ACCOUNT_NAME = m_Fields("FLD_ACCOUNT_NAME")
End Property
Public Property Get K_AMOUNT() As String
    ' [計算欄位] 金額
    K_AMOUNT = m_Fields("K_AMOUNT")
End Property

