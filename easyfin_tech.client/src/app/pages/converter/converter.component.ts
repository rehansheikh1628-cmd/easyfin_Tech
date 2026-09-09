import { Component, ChangeDetectorRef } from '@angular/core';
import { HttpEvent, HttpEventType } from '@angular/common/http';
import { 
  StatementService, 
  StatementUploadResponse,
  PdfExtractionResult,
  PdfPageResult,
  TransactionReviewDto,
  StatementValidationSummaryDto,
  CorrectTransactionRequest,
  StatementTransactionsResponse
} from '../../services/statement.service';

export type UploadState = 'IDLE' | 'FILE_SELECTED' | 'VALIDATING' | 'UPLOADING' | 'UPLOADED' | 'ERROR';
export type ExtractionState = 'IDLE' | 'EXTRACTING' | 'EXTRACTED' | 'NO_TEXT' | 'PASSWORD_REQUIRED' | 'ERROR';
export type ParseState = 'IDLE' | 'PARSING' | 'PARSED' | 'ERROR';

export interface StatementTransaction {
  id: string;
  date: string;
  valueDate: string;
  narration: string;
  reference: string;
  debit: number | null;
  credit: number | null;
  balance: number;
  status: 'verified' | 'flagged';
}

@Component({
  selector: 'app-converter',
  templateUrl: './converter.component.html',
  styleUrls: ['./converter.component.css'],
  standalone: false
})
export class ConverterComponent {
  currentStep = 1;
  selectedFile: File | null = null;
  selectedBank = 'auto';
  statementPassword = '';
  isDragging = false;

  // Real Ingestion States (Phase 2)
  uploadState: UploadState = 'IDLE';
  uploadProgress = 0;
  uploadError: string | null = null;
  uploadResult: StatementUploadResponse | null = null;

  // Real Extraction States (Phase 3)
  extractionState: ExtractionState = 'IDLE';
  extractionResult: PdfExtractionResult | null = null;
  extractionError: string | null = null;
  selectedInspectionTab: 'summary' | 'rawText' | 'textBlocks' | 'candidateRows' | 'candidateTables' = 'summary';
  selectedPageNumber = 1;
  wrapRawText = false;

  // Real Parsing & Validation States (Phase 4 & Phase 5)
  parseState: ParseState = 'IDLE';
  parseResult: any = null;
  parseError: string | null = null;

  // Review Table & Validation Summary
  reviewTransactions: TransactionReviewDto[] = [];
  validationSummary: StatementValidationSummaryDto | null = null;
  isLoadingReview = false;
  reviewError: string | null = null;
  reviewStatusFilter: 'all' | 'valid' | 'review' | 'invalid' | 'corrected' = 'all';
  reviewTypeFilter: 'all' | 'debit' | 'credit' = 'all';
  reviewSearch = '';
  reviewCurrentPage = 1;
  reviewPageSize = 50;
  reviewTotalCount = 0;
  reviewTotalPages = 1;

  // Edit / Correction Modal State
  isEditingTransaction = false;
  editingTransaction: TransactionReviewDto | null = null;
  editForm = {
    transactionDate: '',
    valueDate: '',
    description: '',
    reference: '',
    debit: null as number | null,
    credit: null as number | null,
    amount: 0,
    balance: null as number | null,
    transactionType: 'Debit',
    reason: ''
  };
  editError: string | null = null;
  isSavingCorrection = false;
  successNotice: string | null = null;
  isExportingExcel = false;
  exportError: string | null = null;

  // Review & Sample Data
  searchQuery = '';
  typeFilter: 'all' | 'debit' | 'credit' = 'all';

  sampleTransactions: StatementTransaction[] = [
    {
      id: 'TXN-001',
      date: '01/08/2026',
      valueDate: '01/08/2026',
      narration: 'NEFT CR-HDFC0000240-ALPHA TECHNOLOGIES PVT LTD',
      reference: 'N2140029104',
      debit: null,
      credit: 150000.00,
      balance: 342850.00,
      status: 'verified'
    },
    {
      id: 'TXN-002',
      date: '03/08/2026',
      valueDate: '03/08/2026',
      narration: 'UPI-AWS CLOUD HOSTING-BILLING@AMAZON',
      reference: 'UPI/62189021',
      debit: 14890.00,
      credit: null,
      balance: 327960.00,
      status: 'verified'
    },
    {
      id: 'TXN-003',
      date: '05/08/2026',
      valueDate: '05/08/2026',
      narration: 'ACH DR-COMMERCIAL LEASE AUGUST 2026',
      reference: 'ACH721094',
      debit: 45000.00,
      credit: null,
      balance: 282960.00,
      status: 'verified'
    },
    {
      id: 'TXN-004',
      date: '08/08/2026',
      valueDate: '08/08/2026',
      narration: 'RTGS CR-SBI000412-CLIENT RETENTION QUARTER 2',
      reference: 'R410098210',
      debit: null,
      credit: 210000.00,
      balance: 492960.00,
      status: 'verified'
    },
    {
      id: 'TXN-005',
      date: '10/08/2026',
      valueDate: '10/08/2026',
      narration: 'IMPS P2A-AIRTEL BROADBAND FIBER LEASED LINE',
      reference: 'IMP901238',
      debit: 3450.00,
      credit: null,
      balance: 489510.00,
      status: 'verified'
    },
    {
      id: 'TXN-006',
      date: '12/08/2026',
      valueDate: '12/08/2026',
      narration: 'SALARY DISBURSEMENT-AUGUST BATCH 1',
      reference: 'CMS881920',
      debit: 185000.00,
      credit: null,
      balance: 304510.00,
      status: 'verified'
    },
    {
      id: 'TXN-007',
      date: '15/08/2026',
      valueDate: '15/08/2026',
      narration: 'GST PAYMENT TAX DEPOSIT-CHALLAN 082026',
      reference: 'CPIN992104',
      debit: 32400.00,
      credit: null,
      balance: 272110.00,
      status: 'verified'
    },
    {
      id: 'TXN-008',
      date: '18/08/2026',
      valueDate: '18/08/2026',
      narration: 'INTEREST CREDIT-SAVINGS ACCOUNT SB-00214',
      reference: 'INT-Q2-26',
      debit: null,
      credit: 4250.00,
      balance: 276360.00,
      status: 'verified'
    }
  ];

  transactions: StatementTransaction[] = [];

  constructor(
    public statementService: StatementService,
    private cdr: ChangeDetectorRef
  ) {}

  onFileDropped(event: DragEvent): void {
    event.preventDefault();
    this.isDragging = false;
    if (event.dataTransfer && event.dataTransfer.files.length > 0) {
      this.handleFileSelection(event.dataTransfer.files[0]);
    }
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.isDragging = true;
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    this.isDragging = false;
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.handleFileSelection(input.files[0]);
    }
    input.value = '';
  }

  handleFileSelection(file: File): void {
    this.uploadError = null;
    this.uploadResult = null;
    this.uploadProgress = 0;
    this.uploadState = 'VALIDATING';
    this.cdr.markForCheck();

    // Client-side Validation: Extension
    if (!file.name.toLowerCase().endsWith('.pdf')) {
      this.uploadError = 'Please select a valid PDF file (.pdf).';
      this.uploadState = 'ERROR';
      this.selectedFile = null;
      this.cdr.markForCheck();
      return;
    }

    // Client-side Validation: Non-empty
    if (file.size === 0) {
      this.uploadError = 'The selected file is empty.';
      this.uploadState = 'ERROR';
      this.selectedFile = null;
      this.cdr.markForCheck();
      return;
    }

    // Client-side Validation: 50MB Limit
    const maxSizeBytes = 50 * 1024 * 1024;
    if (file.size > maxSizeBytes) {
      this.uploadError = 'This file is larger than the allowed upload size of 50 MB.';
      this.uploadState = 'ERROR';
      this.selectedFile = null;
      this.cdr.markForCheck();
      return;
    }

    this.selectedFile = file;
    this.uploadState = 'FILE_SELECTED';
    this.cdr.markForCheck();
  }

  startUpload(): void {
    if (!this.selectedFile) return;

    this.uploadState = 'UPLOADING';
    this.uploadProgress = 0;
    this.uploadError = null;
    this.uploadResult = null;
    this.extractionState = 'IDLE';
    this.extractionResult = null;
    this.extractionError = null;
    this.cdr.markForCheck();

    this.statementService.uploadStatement(this.selectedFile).subscribe({
      next: (event: HttpEvent<StatementUploadResponse>) => {
        switch (event.type) {
          case HttpEventType.Sent:
            this.uploadProgress = 0;
            this.cdr.markForCheck();
            break;

          case HttpEventType.UploadProgress:
            if (event.total && event.total > 0) {
              this.uploadProgress = Math.min(Math.round((100 * event.loaded) / event.total), 100);
            }
            this.cdr.markForCheck();
            break;

          case HttpEventType.Response:
            this.uploadProgress = 100;
            this.uploadResult = event.body;
            this.uploadState = 'UPLOADED';
            this.cdr.markForCheck();
            break;
        }
      },
      error: (err: any) => {
        this.uploadState = 'ERROR';
        if (err?.error?.detail && typeof err.error.detail === 'string') {
          this.uploadError = err.error.detail;
        } else if (err?.error?.title && typeof err.error.title === 'string') {
          this.uploadError = err.error.title;
        } else if (err?.status === 0) {
          this.uploadError = 'Upload failed. Unable to connect to backend server. Please verify the service is running.';
        } else {
          this.uploadError = 'Upload failed. Please check network connectivity and try again.';
        }
        this.cdr.markForCheck();
      },
      complete: () => {
        // Defensive safeguard: ensure the component is NEVER left permanently stuck in UPLOADING
        if (this.uploadState === 'UPLOADING') {
          if (this.uploadResult) {
            this.uploadState = 'UPLOADED';
            this.uploadProgress = 100;
          } else {
            this.uploadState = 'ERROR';
            this.uploadError = this.uploadError || 'Upload stream completed without server confirmation.';
          }
          this.cdr.markForCheck();
        }
      }
    });
  }

  triggerExtraction(): void {
    if (!this.uploadResult?.fileId) return;

    this.extractionState = 'EXTRACTING';
    this.extractionError = null;
    this.cdr.markForCheck();

    this.statementService.extractStatement(this.uploadResult.fileId, this.statementPassword || undefined).subscribe({
      next: (result: PdfExtractionResult) => {
        this.extractionResult = result;
        this.selectedPageNumber = 1;

        if (result.extractionStatus === 'PasswordProtected') {
          this.extractionState = 'PASSWORD_REQUIRED';
          this.extractionError = result.errors?.[0] || 'This PDF is password-protected. Please provide the document password in statement options.';
        } else if (result.extractionStatus === 'NoDigitalTextDetected' || !result.hasUsableText) {
          this.extractionState = 'NO_TEXT';
          // Navigate to Step 2 to let user inspect the no-text diagnostics
          this.currentStep = 2;
        } else if (result.extractionStatus === 'Failed') {
          this.extractionState = 'ERROR';
          this.extractionError = result.errors?.[0] || 'PDF structure extraction failed.';
        } else {
          this.extractionState = 'EXTRACTED';
          // Navigate to Step 2 for structural inspection
          this.currentStep = 2;
        }
        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.extractionState = 'ERROR';
        if (err?.error?.detail && typeof err.error.detail === 'string') {
          this.extractionError = err.error.detail;
        } else if (err?.error?.title && typeof err.error.title === 'string') {
          this.extractionError = err.error.title;
        } else {
          this.extractionError = 'PDF extraction request failed. Please check server logs.';
        }
        this.cdr.markForCheck();
      }
    });
  }

  get selectedPageResult(): PdfPageResult | null {
    if (!this.extractionResult || !this.extractionResult.pages || this.extractionResult.pages.length === 0) {
      return null;
    }
    return this.extractionResult.pages.find(p => p.pageNumber === this.selectedPageNumber) || this.extractionResult.pages[0];
  }

  selectInspectionPage(pageNumber: number): void {
    this.selectedPageNumber = pageNumber;
    this.cdr.markForCheck();
  }

  setInspectionTab(tab: 'summary' | 'rawText' | 'textBlocks' | 'candidateRows' | 'candidateTables'): void {
    this.selectedInspectionTab = tab;
    this.cdr.markForCheck();
  }

  triggerParsing(): void {
    if (!this.uploadResult?.fileId) return;

    this.parseState = 'PARSING';
    this.parseError = null;
    this.cdr.markForCheck();

    this.statementService.parseStatement(this.uploadResult.fileId).subscribe({
      next: (res: any) => {
        this.parseResult = res;
        this.parseState = 'PARSED';
        // Automatically load real parsed transactions and move to Step 3
        this.loadReviewTransactions(1);
        this.currentStep = 3;
        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.parseState = 'ERROR';
        if (err?.error?.detail) {
          this.parseError = err.error.detail;
        } else if (err?.error?.title) {
          this.parseError = err.error.title;
        } else {
          this.parseError = 'Statement parsing failed. Please verify bank format support.';
        }
        this.cdr.markForCheck();
      }
    });
  }

  loadReviewTransactions(page = 1): void {
    if (!this.uploadResult?.fileId) return;

    this.isLoadingReview = true;
    this.reviewError = null;
    this.reviewCurrentPage = page;
    this.cdr.markForCheck();

    this.statementService.getTransactions(this.uploadResult.fileId, {
      page: this.reviewCurrentPage,
      pageSize: this.reviewPageSize,
      status: this.reviewStatusFilter,
      type: this.reviewTypeFilter,
      search: this.reviewSearch || undefined
    }).subscribe({
      next: (res: StatementTransactionsResponse) => {
        this.reviewTransactions = res.items || [];
        this.reviewTotalCount = res.totalCount || 0;
        this.reviewTotalPages = res.totalPages || 1;
        if (res.summary) {
          this.validationSummary = res.summary;
        }
        this.isLoadingReview = false;
        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.isLoadingReview = false;
        this.reviewError = err?.error?.detail || 'Failed to load statement transactions.';
        this.cdr.markForCheck();
      }
    });
  }

  setReviewStatusFilter(filter: 'all' | 'valid' | 'review' | 'invalid' | 'corrected'): void {
    this.reviewStatusFilter = filter;
    this.loadReviewTransactions(1);
  }

  setReviewTypeFilter(filter: 'all' | 'debit' | 'credit'): void {
    this.reviewTypeFilter = filter;
    this.loadReviewTransactions(1);
  }

  onReviewSearchChange(): void {
    this.loadReviewTransactions(1);
  }

  goToReviewPage(page: number): void {
    if (page < 1 || page > this.reviewTotalPages || page === this.reviewCurrentPage) return;
    this.loadReviewTransactions(page);
  }

  openEditTransaction(txn: TransactionReviewDto): void {
    this.editingTransaction = txn;
    this.editError = null;
    this.successNotice = null;
    this.isEditingTransaction = true;

    // Populate edit form
    const dateStr = txn.transactionDate ? txn.transactionDate.substring(0, 10) : '';
    this.editForm = {
      transactionDate: dateStr,
      valueDate: txn.valueDate ? txn.valueDate.substring(0, 10) : dateStr,
      description: txn.description || '',
      reference: txn.reference || '',
      debit: txn.debit ?? null,
      credit: txn.credit ?? null,
      amount: txn.amount || 0,
      balance: txn.balance ?? null,
      transactionType: txn.transactionType || (txn.debit ? 'Debit' : 'Credit'),
      reason: ''
    };
    this.cdr.markForCheck();
  }

  closeEditModal(): void {
    this.isEditingTransaction = false;
    this.editingTransaction = null;
    this.editError = null;
    this.cdr.markForCheck();
  }

  submitCorrection(): void {
    if (!this.editingTransaction || !this.uploadResult?.fileId) return;

    if (!this.editForm.reason || !this.editForm.reason.trim()) {
      this.editError = 'A reason for correction is required for audit traceability.';
      this.cdr.markForCheck();
      return;
    }

    if (!this.editForm.description || !this.editForm.description.trim()) {
      this.editError = 'Description cannot be empty.';
      this.cdr.markForCheck();
      return;
    }

    if (!this.editForm.transactionDate) {
      this.editError = 'A valid transaction date is required.';
      this.cdr.markForCheck();
      return;
    }

    const hasDebit = this.editForm.debit !== null && this.editForm.debit > 0;
    const hasCredit = this.editForm.credit !== null && this.editForm.credit > 0;

    if (hasDebit && hasCredit) {
      this.editError = 'Cannot enter both Debit and Credit amounts.';
      this.cdr.markForCheck();
      return;
    }

    if (!hasDebit && !hasCredit) {
      this.editError = 'Either Debit or Credit must be greater than zero.';
      this.cdr.markForCheck();
      return;
    }

    this.isSavingCorrection = true;
    this.editError = null;
    this.cdr.markForCheck();

    const amt = hasDebit ? (this.editForm.debit || 0) : (this.editForm.credit || 0);
    const req: CorrectTransactionRequest = {
      transactionDate: this.editForm.transactionDate,
      valueDate: this.editForm.valueDate || undefined,
      description: this.editForm.description.trim(),
      debit: this.editForm.debit,
      credit: this.editForm.credit,
      amount: amt,
      balance: this.editForm.balance,
      reference: this.editForm.reference ? this.editForm.reference.trim() : null,
      transactionType: hasDebit ? 'Debit' : 'Credit',
      reason: this.editForm.reason.trim()
    };

    this.statementService.correctTransaction(this.uploadResult.fileId, this.editingTransaction.id, req).subscribe({
      next: (updatedTxn: TransactionReviewDto) => {
        this.isSavingCorrection = false;
        this.isEditingTransaction = false;
        this.editingTransaction = null;
        this.successNotice = `Transaction ${updatedTxn.id.substring(0, 8)} corrected successfully.`;

        // Update transaction in list
        const idx = this.reviewTransactions.findIndex(t => t.id === updatedTxn.id);
        if (idx !== -1) {
          this.reviewTransactions[idx] = updatedTxn;
        }

        // Refresh validation summary
        if (this.uploadResult?.fileId) {
          this.statementService.getValidationSummary(this.uploadResult.fileId).subscribe({
            next: (sum) => {
              this.validationSummary = sum;
              this.cdr.markForCheck();
            }
          });
        }

        // Auto-dismiss notice after 4 seconds
        setTimeout(() => {
          this.successNotice = null;
          this.cdr.markForCheck();
        }, 4000);

        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.isSavingCorrection = false;
        this.editError = err?.error?.detail || err?.error?.title || 'Failed to save transaction correction.';
        this.cdr.markForCheck();
      }
    });
  }

  resetUpload(): void {
    this.selectedFile = null;
    this.uploadState = 'IDLE';
    this.uploadProgress = 0;
    this.uploadError = null;
    this.uploadResult = null;
    this.extractionState = 'IDLE';
    this.extractionResult = null;
    this.extractionError = null;
    this.statementPassword = '';
    this.selectedPageNumber = 1;
    this.selectedInspectionTab = 'summary';
    this.parseState = 'IDLE';
    this.parseResult = null;
    this.parseError = null;
    this.reviewTransactions = [];
    this.validationSummary = null;
    this.isEditingTransaction = false;
    this.editingTransaction = null;
    this.successNotice = null;
    this.isExportingExcel = false;
    this.exportError = null;
    this.cdr.markForCheck();
  }

  loadSampleData(): void {
    this.transactions = [...this.sampleTransactions];
    this.currentStep = 3;
  }

  setStep(step: number): void {
    this.currentStep = step;
  }

  get totalCredits(): number {
    return this.transactions.reduce((acc, t) => acc + (t.credit || 0), 0);
  }

  get totalDebits(): number {
    return this.transactions.reduce((acc, t) => acc + (t.debit || 0), 0);
  }

  get netChange(): number {
    return this.totalCredits - this.totalDebits;
  }

  get filteredTransactions(): StatementTransaction[] {
    return this.transactions.filter(t => {
      const matchesSearch = !this.searchQuery || 
        t.narration.toLowerCase().includes(this.searchQuery.toLowerCase()) ||
        t.reference.toLowerCase().includes(this.searchQuery.toLowerCase()) ||
        t.date.includes(this.searchQuery);

      if (!matchesSearch) return false;

      if (this.typeFilter === 'debit') return t.debit !== null;
      if (this.typeFilter === 'credit') return t.credit !== null;
      return true;
    });
  }

  downloadExcel(): void {
    if (!this.uploadResult?.fileId) {
      this.exportDemoExcel();
      return;
    }

    if (this.validationSummary && this.validationSummary.invalidCount > 0) {
      this.exportError = `Statement contains ${this.validationSummary.invalidCount} invalid transaction(s). Please review and correct all invalid transactions before exporting.`;
      this.cdr.markForCheck();
      return;
    }

    this.isExportingExcel = true;
    this.exportError = null;
    this.cdr.markForCheck();

    this.statementService.exportExcel(this.uploadResult.fileId).subscribe({
      next: (blob: Blob) => {
        this.isExportingExcel = false;
        const bankName = (this.validationSummary?.bankName || 'Statement').replace(/[^a-zA-Z0-9_-]/g, '_');
        const dateStr = new Date().toISOString().substring(0, 10).replace(/-/g, '');
        const filename = `EasyFin_${bankName}_${dateStr}.xlsx`;

        const link = document.createElement('a');
        link.href = URL.createObjectURL(blob);
        link.setAttribute('download', filename);
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        setTimeout(() => URL.revokeObjectURL(link.href), 1000);

        this.successNotice = 'Excel workbook exported successfully.';
        setTimeout(() => {
          this.successNotice = null;
          this.cdr.markForCheck();
        }, 4000);

        this.cdr.markForCheck();
      },
      error: async (err: any) => {
        this.isExportingExcel = false;
        if (err?.error instanceof Blob) {
          try {
            const errorText = await err.error.text();
            const json = JSON.parse(errorText);
            this.exportError = json?.detail || json?.title || 'Failed to export statement to Excel.';
          } catch {
            this.exportError = 'Failed to export statement to Excel.';
          }
        } else {
          this.exportError = err?.error?.detail || err?.error?.title || 'Failed to export statement to Excel.';
        }
        this.cdr.markForCheck();
      }
    });
  }

  exportDemoExcel(): void {
    const headers = ['Date', 'Value Date', 'Particulars / Narration', 'Reference/UTR', 'Debit', 'Credit', 'Balance', 'Status'];
    const rows = this.transactions.map(t => [
      t.date,
      t.valueDate,
      `"${t.narration.replace(/"/g, '""')}"`,
      t.reference,
      t.debit ? t.debit.toFixed(2) : '',
      t.credit ? t.credit.toFixed(2) : '',
      t.balance.toFixed(2),
      t.status
    ]);

    const csvContent = [headers.join(','), ...rows.map(r => r.join(','))].join('\r\n');
    const blob = new Blob([csvContent], { type: 'text/csv;charset=utf-8;' });
    const link = document.createElement('a');
    link.href = URL.createObjectURL(blob);
    link.setAttribute('download', 'EasyFin_Statement_Export.csv');
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
  }
}
