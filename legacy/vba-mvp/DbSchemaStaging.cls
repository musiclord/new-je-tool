VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "DbSchemaStaging"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Schema
' Name:     Staging
' Purpose:  接續資料來源層，定義 ETL 過程中的中間表 (Intermediate Tables)
'
'===============================================================================
'--
Private m_JeInPeriod As TypeEtlJeInPeriod
Private m_JeNotInPeriod As TypeEtlJeNotInPeriod
Private m_AccountSum As TypeEtlAccountSum
Private m_CompletenessCal As TypeEtlCompletenessCal
Private m_CompletenessDiff As TypeEtlCompletenessDiff
Private m_CompletenessDetail As TypeEtlCompletenessDetail
Private m_DocBalanceSum As TypeEtlDocBalanceSum
Private m_DocBalanceDiff As TypeEtlDocBalanceDiff
Private m_DocBalanceDetail As TypeEtlDocBalanceDetail
Private m_InfRandom As TypeEtlInfRandom
Private m_InfSorted As TypeEtlInfSorted
Private m_NullAccount As TypeEtlNullAccount
Private m_NullDoc As TypeEtlNullDoc
Private m_NullDesc As TypeEtlNullDesc

'-------------------------------------------------------------------------------
'-- 初始化
'-------------------------------------------------------------------------------
Public Sub Initialize()
    With m_JeInPeriod
        .Name = "JE_IN_PERIOD"
    End With
    
    With m_JeNotInPeriod
        .Name = "JE_NOT_IN_PERIOD"
    End With
    
    With m_AccountSum
        .Name = "ACCOUNT_SUM"
    End With
    
    With m_CompletenessCal
        .Name = "COMPLETENESS_CALCULATED"
        .AccountMerged = "K_ACCOUNT_MERGED"
        .Diff = "K_TB_JE_DIFF"
    End With
    
    With m_CompletenessDiff
        .Name = "COMPLETENESS_DIFF"
    End With
    
    With m_CompletenessDetail
        .Name = "COMPLETENESS_DETAIL"
    End With
    
    With m_InfRandom
        .Name = "INF_RANDOM"
    End With
    
    With m_InfSorted
        .Name = "INF_SORTED"
    End With
    
    With m_NullAccount
        .Name = "NULL_ACCOUNT"
    End With
    
    With m_NullDoc
        .Name = "NULL_DOCUMENT"
    End With
    
    With m_NullDesc
        .Name = "NULL_DESCRIPTION"
    End With
End Sub

'-------------------------------------------------------------------------------
'-- Staging Layer
'-------------------------------------------------------------------------------
Public Property Get JeInPeriod() As TypeEtlJeInPeriod
    JeInPeriod = m_JeInPeriod
End Property

Public Property Get JeNotInPeriod() As TypeEtlJeNotInPeriod
    JeNotInPeriod = m_JeNotInPeriod
End Property

Public Property Get AccountSum() As TypeEtlAccountSum
    AccountSum = m_AccountSum
End Property

Public Property Get CompletenessCal() As TypeEtlCompletenessCal
    CompletenessCal = m_CompletenessCal
End Property

Public Property Get CompletenessDiff() As TypeEtlCompletenessDiff
    CompletenessDiff = m_CompletenessDiff
End Property

Public Property Get CompletenessDetail() As TypeEtlCompletenessDetail
    CompletenessDetail = m_CompletenessDetail
End Property

Public Property Get DocBalanceSum() As TypeEtlDocBalanceSum
    DocBalanceSum = m_DocBalanceSum
End Property

Public Property Get DocBalanceDiff() As TypeEtlDocBalanceDiff
    DocBalanceDiff = m_DocBalanceDiff
End Property

Public Property Get DocBalanceDetail() As TypeEtlDocBalanceDetail
    DocBalanceDetail = m_DocBalanceDetail
End Property

Public Property Get InfRandom() As TypeEtlInfRandom
    InfRandom = m_InfRandom
End Property

Public Property Get InfSorted() As TypeEtlInfSorted
    InfSorted = m_InfSorted
End Property

Public Property Get NullAccount() As TypeEtlNullAccount
    NullAccount = m_NullAccount
End Property

Public Property Get NullDoc() As TypeEtlNullDoc
    NullDoc = m_NullDoc
End Property

Public Property Get NullDesc() As TypeEtlNullDesc
    NullDesc = m_NullDesc
End Property

'-------------------------------------------------------------------------------
'-- DDL (Data Definition Language)
'-------------------------------------------------------------------------------
