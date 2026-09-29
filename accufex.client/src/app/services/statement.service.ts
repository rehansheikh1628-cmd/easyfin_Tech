import { Injectable } from '@angular/core';
import { HttpClient, HttpEvent, HttpRequest } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface StatementUploadResponse {
  success: boolean;
  message: string;
  fileId: string;
  jobId?: string;
  originalFileName: string;
  storedFileName: string;
  fileSizeBytes: number;
  fileSizeFormatted: string;
  contentType: string;
  uploadedAt: string;
  status: string;
  processingStatus: number;
  isDuplicate: boolean;
  duplicateNotice?: string;
  fileHash: string;
  clientId: string;
  financialYearId: string;
}

export interface StatementJobStatusDto {
  jobId: string;
  fileId: string;
  fileName: string;
  status: 'Queued' | 'Processing' | 'Completed' | 'Failed' | 'Cancelled' | 'RequiresPassword' | string;
  statusCode: number;
  stage: string;
  progressPercent: number;
  errorMessage?: string | null;
  enqueuedAt?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
  isTerminal: boolean;
  requiresPassword: boolean;
  transactionCount?: number | null;
  detectedBankName?: string | null;
}

export interface StatementSummaryDto {
  id: string;
  originalFileName: string;
  fileSizeBytes: number;
  fileSizeFormatted: string;
  uploadedAt: string;
  status: string;
  processingStatus: number;
  processingError?: string;
  clientId: string;
  clientName?: string;
  financialYearId: string;
  financialYearName?: string;
}

export interface StatementDetailDto {
  id: string;
  originalFileName: string;
  storedFileName: string;
  extension: string;
  contentType: string;
  fileSizeBytes: number;
  fileSizeFormatted: string;
  uploadedAt: string;
  updatedAt?: string;
  status: string;
  processingStatus: number;
  processingError?: string;
  clientId: string;
  clientName?: string;
  financialYearId: string;
  financialYearName?: string;
}

export interface PdfBoundingBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface PdfTextBlock {
  text: string;
  x: number;
  y: number;
  width: number;
  height: number;
  pageNumber: number;
  readingOrderIndex: number;
}

export interface PdfCandidateCell {
  columnIndex: number;
  text: string;
  boundingBox: PdfBoundingBox;
}

export interface PdfCandidateColumn {
  columnIndex: number;
  leftX: number;
  rightX: number;
  headerText?: string;
}

export interface PdfCandidateRow {
  rowIndex: number;
  pageNumber: number;
  y: number;
  height: number;
  rawLineText: string;
  cells: PdfCandidateCell[];
  isHeader: boolean;
  isFooter: boolean;
  lineFragments: PdfTextBlock[];
}

export interface PdfCandidateTable {
  tableIndex: number;
  pageNumber: number;
  boundingBox: PdfBoundingBox;
  columnCount: number;
  rowCount: number;
  columns: PdfCandidateColumn[];
  rows: PdfCandidateRow[];
  confidence: number;
}

export interface PdfPageResult {
  pageNumber: number;
  width: number;
  height: number;
  rawText: string;
  wordCount: number;
  characterCount: number;
  hasUsableText: boolean;
  textBlocks: PdfTextBlock[];
  candidateRows: PdfCandidateRow[];
  candidateTables: PdfCandidateTable[];
  repeatedHeaders: string[];
  repeatedFooters: string[];
}

export interface PdfExtractionResult {
  fileId: string;
  originalFileName: string;
  pageCount: number;
  extractionStatus: 'DigitalTextExtracted' | 'NoDigitalTextDetected' | 'PasswordProtected' | 'Failed' | string;
  startedAt: string;
  completedAt: string;
  durationMs: number;
  pdfType: string;
  hasUsableText: boolean;
  characterCount: number;
  wordCount: number;
  textBlockCount: number;
  candidateRowCount: number;
  candidateTableCount: number;
  pages: PdfPageResult[];
  warnings: string[];
  errors: string[];
  storageArtifactPath?: string;
}

@Injectable({
  providedIn: 'root'
})
export class StatementService {
  private readonly baseUrl = environment.apiUrl && !environment.apiUrl.includes('YOUR-PRODUCTION')
    ? `${environment.apiUrl}/api/statements`
    : '/api/statements';

  constructor(private http: HttpClient) {}

  uploadStatement(file: File, clientId?: string, financialYearId?: string): Observable<HttpEvent<StatementUploadResponse>> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    if (clientId) formData.append('clientId', clientId);
    if (financialYearId) formData.append('financialYearId', financialYearId);

    const req = new HttpRequest('POST', `${this.baseUrl}/upload`, formData, {
      reportProgress: true
    });

    return this.http.request<StatementUploadResponse>(req);
  }

  getStatements(): Observable<StatementSummaryDto[]> {
    return this.http.get<StatementSummaryDto[]>(this.baseUrl);
  }

  getStatementById(id: string): Observable<StatementDetailDto> {
    return this.http.get<StatementDetailDto>(`${this.baseUrl}/${id}`);
  }

  downloadStatement(id: string): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/${id}/download`, {
      responseType: 'blob'
    });
  }

  downloadStatementFile(id: string, fileName: string): void {
    this.downloadStatement(id).subscribe({
      next: (blob: Blob) => {
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        window.URL.revokeObjectURL(url);
      },
      error: (err: unknown) => {
        console.error('Failed to download statement', err);
      }
    });
  }

  deleteStatement(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }

  extractStatement(id: string, password?: string): Observable<PdfExtractionResult> {
    const body = password ? { password } : {};
    return this.http.post<PdfExtractionResult>(`${this.baseUrl}/${id}/extract`, body);
  }

  getStatementExtraction(id: string): Observable<PdfExtractionResult> {
    return this.http.get<PdfExtractionResult>(`${this.baseUrl}/${id}/extraction`);
  }

  parseStatement(id: string): Observable<any> {
    return this.http.post<any>(`${this.baseUrl}/${id}/parse`, {});
  }

  getTransactions(id: string, options?: {
    page?: number;
    pageSize?: number;
    status?: string;
    type?: string;
    search?: string;
    sortBy?: string;
    sortDesc?: boolean;
  }): Observable<StatementTransactionsResponse> {
    let params: any = {};
    if (options) {
      if (options.page !== undefined) params.page = options.page.toString();
      if (options.pageSize !== undefined) params.pageSize = options.pageSize.toString();
      if (options.status) params.status = options.status;
      if (options.type) params.type = options.type;
      if (options.search) params.search = options.search;
      if (options.sortBy) params.sortBy = options.sortBy;
      if (options.sortDesc !== undefined) params.sortDesc = options.sortDesc.toString();
    }
    return this.http.get<StatementTransactionsResponse>(`${this.baseUrl}/${id}/transactions`, { params });
  }

  getTransactionById(statementId: string, transactionId: string): Observable<TransactionReviewDto> {
    return this.http.get<TransactionReviewDto>(`${this.baseUrl}/${statementId}/transactions/${transactionId}`);
  }

  correctTransaction(statementId: string, transactionId: string, request: CorrectTransactionRequest): Observable<TransactionReviewDto> {
    return this.http.put<TransactionReviewDto>(`${this.baseUrl}/${statementId}/transactions/${transactionId}`, request);
  }

  getValidationSummary(statementId: string): Observable<StatementValidationSummaryDto> {
    return this.http.get<StatementValidationSummaryDto>(`${this.baseUrl}/${statementId}/validation-summary`);
  }

  revalidateStatement(statementId: string): Observable<StatementValidationSummaryDto> {
    return this.http.post<StatementValidationSummaryDto>(`${this.baseUrl}/${statementId}/revalidate`, {});
  }

  exportExcel(id: string): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/${id}/export/excel`, {
      responseType: 'blob'
    });
  }

  getJobStatus(fileId: string): Observable<StatementJobStatusDto> {
    return this.http.get<StatementJobStatusDto>(`${this.baseUrl}/${fileId}/job-status`);
  }

  cancelJob(fileId: string): Observable<{ message: string; jobId: string }> {
    return this.http.post<{ message: string; jobId: string }>(`${this.baseUrl}/${fileId}/cancel`, {});
  }

  retryJob(fileId: string): Observable<StatementJobStatusDto> {
    return this.http.post<StatementJobStatusDto>(`${this.baseUrl}/${fileId}/retry`, {});
  }

  unlockJob(fileId: string, password: string): Observable<StatementJobStatusDto> {
    return this.http.post<StatementJobStatusDto>(`${this.baseUrl}/${fileId}/unlock`, { password });
  }

  formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(2)} KB`;
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
  }
}

export interface FieldChangeDto {
  fieldName: string;
  oldValue: string | null;
  newValue: string | null;
}

export interface CorrectionHistoryEntryDto {
  timestampUtc: string;
  userId: string;
  reason: string;
  changes: FieldChangeDto[];
}

export interface OriginalTransactionSnapshotDto {
  transactionId: string;
  transactionDate: string;
  valueDate?: string | null;
  description: string;
  debit?: number | null;
  credit?: number | null;
  amount: number;
  balance?: number | null;
  reference?: string | null;
  utr?: string | null;
  transactionType?: string | null;
  bankCode: number;
  account?: string | null;
  sourcePageNumber: number;
  sourceLineIndex: number;
  parserVersion: string;
  processingWarning?: string | null;
}

export interface TransactionReviewDto {
  id: string;
  transactionDate: string;
  valueDate?: string | null;
  description: string;
  debit?: number | null;
  credit?: number | null;
  amount: number;
  balance?: number | null;
  reference?: string | null;
  utr?: string | null;
  transactionType: string;
  bankCode: number;
  bankName: string;
  account?: string | null;
  sourceFileId: string;
  sourcePageNumber: number;
  sourceLineIndex: number;
  parserVersion: string;
  parserWarning?: string | null;

  validationStatus: 'VALID' | 'REVIEW' | 'INVALID' | 'CORRECTED';
  validationSeverity: 'INFO' | 'WARNING' | 'ERROR';
  validationErrors: string[];
  validationWarnings: string[];
  balanceStatus: 'BALANCED' | 'MISMATCH' | 'NOT_CHECKABLE';
  isDuplicate: boolean;
  isReadyForExport: boolean;

  isCorrected: boolean;
  correctionCount: number;
  originalValues?: OriginalTransactionSnapshotDto | null;
  history: CorrectionHistoryEntryDto[];
}

export interface CorrectTransactionRequest {
  transactionDate: string;
  valueDate?: string | null;
  description: string;
  debit?: number | null;
  credit?: number | null;
  amount: number;
  balance?: number | null;
  reference?: string | null;
  transactionType?: string | null;
  reason: string;
}

export interface StatementValidationSummaryDto {
  statementFileId: string;
  detectedBank: number;
  bankName: string;
  parserVersion: string;
  totalTransactions: number;
  validCount: number;
  reviewCount: number;
  invalidCount: number;
  correctedCount: number;
  debitCount: number;
  creditCount: number;
  totalDebits: number;
  totalCredits: number;
  netMovement: number;
  balanceCheckableCount: number;
  balanceMatchedCount: number;
  balanceMismatchedCount: number;
  duplicateCount: number;
  isReadyForExport: boolean;
}

export interface StatementTransactionsResponse {
  success: boolean;
  bankCode: number;
  bankName: string;
  parserVersion: string;
  totalDetected: number;
  processedCount: number;
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
  items: TransactionReviewDto[];
  transactions: TransactionReviewDto[];
  summary?: StatementValidationSummaryDto;
}
