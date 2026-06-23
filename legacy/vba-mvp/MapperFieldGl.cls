VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "MapperFieldGl"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Mapper
' Name:     FieldGl
' Purpose:  動態管理 GL (General Ledger) 資料表的欄位映射
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
    m_Name = "JE"
    ' 設置初始化欄位陣列
    fields = Array( _
        "FLD_AMOUNT", _
        "FLD_DEBIT_AMOUNT", _
        "FLD_CREDIT_AMOUNT", _
        "FLD_ACCOUNT_CODE", _
        "FLD_ACCOUNT_NAME", _
        "FLD_DOCUMENT_NUMBER", _
        "FLD_DOCUMENT_DESCRIPTION", _
        "FLD_APPROVED_BY", _
        "FLD_CREATED_BY", _
        "FLD_SOURCE_MODULE", _
        "FLD_DR_CR", _
        "FLD_IS_DEBIT", _
        "FLD_IS_MANUAL", _
        "FLD_IS_APPROVED_DATE_AS_LEDGER_DATE", _
        "FLD_POST_DATE", _
        "FLD_APPROVAL_DATE", _
        "FLD_LINE_ITEM", _
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
Public Property Get FLD_AMOUNT() As String
    ' [欄位] 金額
    FLD_AMOUNT = m_Fields("FLD_AMOUNT")
End Property
Public Property Get FLD_DEBIT_AMOUNT() As String
    ' [欄位] 借方
    FLD_DEBIT_AMOUNT = m_Fields("FLD_DEBIT_AMOUNT")
End Property
Public Property Get FLD_CREDIT_AMOUNT() As String
    ' [欄位] 貸方
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
Public Property Get FLD_DOCUMENT_NUMBER() As String
    ' [欄位] 傳票編號
    FLD_DOCUMENT_NUMBER = m_Fields("FLD_DOCUMENT_NUMBER")
End Property
Public Property Get FLD_DOCUMENT_DESCRIPTION() As String
    ' [欄位] 傳票摘要
    FLD_DOCUMENT_DESCRIPTION = m_Fields("FLD_DOCUMENT_DESCRIPTION")
End Property
Public Property Get FLD_APPROVED_BY() As String
    ' [欄位] 傳票核准人員
    FLD_APPROVED_BY = m_Fields("FLD_APPROVED_BY")
End Property
Public Property Get FLD_CREATED_BY() As String
    ' [欄位] 傳票建立人員
    FLD_CREATED_BY = m_Fields("FLD_CREATED_BY")
End Property
Public Property Get FLD_SOURCE_MODULE() As String
    ' [欄位] 分錄來源模組
    FLD_SOURCE_MODULE = m_Fields("FLD_SOURCE_MODULE")
End Property
Public Property Get FLD_DR_CR() As String
    ' [欄位] 借貸別
    FLD_DR_CR = m_Fields("FLD_DR_CR")
End Property
Public Property Get FLD_IS_DEBIT() As String
    ' [欄位] 借方標誌
    FLD_IS_DEBIT = m_Fields("FLD_IS_DEBIT")
End Property
Public Property Get FLD_IS_MANUAL() As String
    ' [欄位] 人工/自動分錄
    FLD_IS_MANUAL = m_Fields("FLD_IS_MANUAL")
End Property
Public Property Get FLD_IS_APPROVED_DATE_AS_LEDGER_DATE() As String
    ' [欄位] 核准日同總帳日
    FLD_IS_APPROVED_DATE_AS_LEDGER_DATE = m_Fields("FLD_IS_APPROVED_DATE_AS_LEDGER_DATE")
End Property
Public Property Get FLD_POST_DATE() As String
    ' [欄位] 總帳日期
    FLD_POST_DATE = m_Fields("FLD_POST_DATE")
End Property
Public Property Get FLD_APPROVAL_DATE() As String
    ' [欄位] 核准日期
    FLD_APPROVAL_DATE = m_Fields("FLD_APPROVAL_DATE")
End Property
Public Property Get FLD_LINE_ITEM() As String
    ' [欄位] 項次
    FLD_LINE_ITEM = m_Fields("FLD_LINE_ITEM")
End Property
Public Property Get K_AMOUNT() As String
    ' [計算欄位] 金額
    K_AMOUNT = m_Fields("K_AMOUNT")
End Property
