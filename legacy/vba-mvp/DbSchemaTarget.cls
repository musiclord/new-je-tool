VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "DbSchemaTarget"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Layer:    Schema
' Name:     Target
' Purpose:  接續 ETL 層，定義最終輸出的報表 (Reports)
'
'===============================================================================

'--
Private m_EngagementOverview As TypeTargetEngagementOverview
Private m_DataOverview As TypeTargetDataOverview
Private m_ValidationOverview As TypeTargetValidationOverview
Private m_CompletenessDetail As TypeTargetCompletenessDetail
Private m_DocumentBalanceDetail As TypeTargetDocumentBalanceDetail
Private m_InfSampleDetail As TypeTargetInfSampleDetail
Private m_AccountMappingInfo As TypeTargetAccountMappingInfo
Private m_FieldMappingInfo As TypeTargetFieldMappingInfo

'-------------------------------------------------------------------------------
'-- 初始化
'-------------------------------------------------------------------------------
Public Sub Initialize()
    '-- ENGAGEMENT_OVERVIEW: 專案總覽報表
    With m_EngagementOverview
        .Name = "ENGAGEMENT_OVERVIEW"
        .Client = "客戶"
        .PrepStartDate = "財報準備期間開始日"
        .PeriodStart = "資料期間開始日"
        .PeriodEnd = "資料期間結束日"
        .PreparedBy = "報表生產人"
        .PreparedDate = "報表生產時間"
    End With
    
    '-- DATA_OVERVIEW: 資料總覽報表
    With m_DataOverview
        .Name = "DATA_OVERVIEW"
        .JeName = "分錄檔案名稱"
        .JeNetAmount = "分錄借貸淨額"
        .JeDebitSum = "分錄借方合計金額"
        .JeCreditSum = "分錄貸方合計金額"
        .JeRecordCount = "分錄筆數"
        .TbName = "試算表檔案名稱"
        .TbAccountCount = "試算表科目數量"
    End With
    
    '-- VALIDATION_OVERVIEW: 驗證總覽報表
    With m_ValidationOverview
        .Name = "VALIDATION_OVERVIEW"
        .NullAccountRecordCount = "無會計科目編號筆數"
        .NullDocumentRecordCount = "無傳票號碼筆數"
        .NullDescriptionRecordCount = "無傳票摘要筆數"
        .NotInPeriodCount = "傳票核准日不在會計期間內筆數"
        .CompletenessDiffCount = "完整性差異筆數"
        .DocumentBalanceDiffCount = "借貸不平差異筆數"
    End With
    
    '-- COMPLETENESS_DETAIL: 完整性測試明細報表
    With m_CompletenessDetail
        .Name = "COMPLETENESS_DETAIL"
    End With
    
    '-- DOCUMENT_BALANCE_DETAIL: 借貸平衡測試明細報表
    With m_DocumentBalanceDetail
        .Name = "DOCUMENT_BALANCE_DETAIL"
    End With
    
    '-- INF_SAMPLE_DETAIL: INF 抽樣明細報表
    With m_InfSampleDetail
        .Name = "INF_SAMPLE_DETAIL"
    End With
    
    '-- ACCOUNT_MAPPING_INFO: 科目配對資訊報表
    With m_AccountMappingInfo
        .Name = "ACCOUNT_MAPPING_INFO"
    End With
    
    '-- FIELD_MAPPING_INFO: 欄位映射資訊報表
    With m_FieldMappingInfo
        .Name = "FIELD_MAPPING_INFO"
    End With
End Sub

'-------------------------------------------------------------------------------
'-- Target Layer
'-------------------------------------------------------------------------------
Public Property Get EngagementOverview() As TypeTargetEngagementOverview
    EngagementOverview = m_EngagementOverview
End Property

Public Property Get DataOverview() As TypeTargetDataOverview
    DataOverview = m_DataOverview
End Property

Public Property Get ValidationOverview() As TypeTargetValidationOverview
    ValidationOverview = m_ValidationOverview
End Property

Public Property Get CompletenessDetail() As TypeTargetCompletenessDetail
    CompletenessDetail = m_CompletenessDetail
End Property

Public Property Get DocumentBalanceDetail() As TypeTargetDocumentBalanceDetail
    DocumentBalanceDetail = m_DocumentBalanceDetail
End Property

Public Property Get InfSampleDetail() As TypeTargetInfSampleDetail
    InfSampleDetail = m_InfSampleDetail
End Property

Public Property Get AccountMappingInfo() As TypeTargetAccountMappingInfo
    AccountMappingInfo = m_AccountMappingInfo
End Property

Public Property Get FieldMappingInfo() As TypeTargetFieldMappingInfo
    FieldMappingInfo = m_FieldMappingInfo
End Property
