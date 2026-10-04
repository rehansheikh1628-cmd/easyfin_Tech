import { Component, OnInit, ChangeDetectorRef, OnDestroy } from '@angular/core';
import { HttpEvent, HttpEventType } from '@angular/common/http';
import { Subscription, timeout, throwError } from 'rxjs';
import { 
  StatementService, 
  StatementUploadResponse, 
  StatementJobStatusDto,
  PdfExtractionResult, 
  PdfPageResult, 
  TransactionReviewDto, 
  StatementValidationSummaryDto, 
  CorrectTransactionRequest, 
  StatementTransactionsResponse,
  UniversalReviewDto,
  UniversalCandidateTransactionDto,
  CorrectUniversalTransactionRequest,
  UpdateUniversalColumnsRequest,
  ApproveUniversalReviewResponse
} from '../../services/statement.service';

export type UploadState = 'IDLE' | 'FILE_SELECTED' | 'VALIDATING' | 'UPLOADING' | 'UPLOADED' | 'ERROR';
export type ExtractionState = 'IDLE' | 'EXTRACTING' | 'EXTRACTED' | 'NO_TEXT' | 'PASSWORD_REQUIRED' | 'ERROR';
export type ParseState = 'IDLE' | 'PARSING' | 'PARSED' | 'ERROR';


@Component({
  selector: 'app-converter',
  templateUrl: './converter.component.html',
  styleUrls: ['./converter.component.css'],
  standalone: false
})
export class ConverterComponent implements OnInit, OnDestroy {
  currentStep = 1;
  selectedFile: File | null = null;
  private uploadSubscription: Subscription | null = null;
  selectedBank = 'auto';
  statementPassword = '';
  isDragging = false;

  // Background Job Processing States (Phase 6)
  activeJobId: string | null = null;
  jobStatus: StatementJobStatusDto | null = null;
  isPollingJob = false;
  isCancellingJob = false;
  isRetryingJob = false;
  private jobPollSubscription: any = null;
  private readonly ACTIVE_JOB_KEY = 'accufex_active_job_id';

  // PDF Password Modal State (On-Demand for Encrypted PDFs)
  showPasswordModal = false;
  modalPassword = '';
  passwordModalError: string | null = null;
  isSubmittingPassword = false;

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

  // Universal Review State (Phase 3)
  isUniversalReview = false;
  universalReview: UniversalReviewDto | null = null;
  isLoadingUniversalReview = false;
  universalReviewError: string | null = null;
  isApprovingReview = false;
  approvalError: string | null = null;

  isEditingCandidate = false;
  editingCandidate: UniversalCandidateTransactionDto | null = null;
  candidateEditForm = {
    date: '',
    valueDate: '',
    description: '',
    reference: '',
    debit: null as number | null,
    credit: null as number | null,
    balance: null as number | null,
    reason: ''
  };
  candidateEditError: string | null = null;
  isSavingCandidate = false;

  isEditingColumns = false;
  editingColumns: any[] = [];
  isSavingColumns = false;


  constructor(
    public statementService: StatementService,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    try {
      const savedJobId = sessionStorage.getItem(this.ACTIVE_JOB_KEY);
      if (savedJobId) {
        this.resumeActiveJob(savedJobId);
      }
    } catch {
      // Ignore in non-browser/storage-restricted environments
    }
  }

  resumeActiveJob(jobId: string): void {
    this.activeJobId = jobId;
    this.startJobTracking(jobId);
  }

  startJobTracking(jobId: string): void {
    this.activeJobId = jobId;
    try {
      sessionStorage.setItem(this.ACTIVE_JOB_KEY, jobId);
    } catch {
      // Ignore in storage-restricted environments
    }
    this.stopJobTracking();

    const poll = () => {
      this.isPollingJob = true;
      this.statementService.getJobStatus(jobId).subscribe({
        next: (status: StatementJobStatusDto) => {
          this.jobStatus = status;
          this.isPollingJob = false;

          // If uploadResult is not set (e.g. on page refresh), synthesize basic details
          if (!this.uploadResult) {
            this.uploadResult = {
              success: true,
              message: 'Session restored',
              fileId: status.fileId,
              jobId: status.jobId,
              originalFileName: status.fileName,
              storedFileName: '',
              fileSizeBytes: 0,
              fileSizeFormatted: '',
              contentType: 'application/pdf',
              uploadedAt: status.enqueuedAt || new Date().toISOString(),
              status: status.status,
              processingStatus: status.statusCode,
              isDuplicate: false,
              fileHash: '',
              clientId: '',
              financialYearId: ''
            };
            this.uploadState = 'UPLOADED';
          }

          if (status.requiresPassword || status.status === 'RequiresPassword') {
            this.stopJobTracking();
            this.extractionState = 'PASSWORD_REQUIRED';
            this.openPasswordModal();
            this.cdr.markForCheck();
            return;
          }

          if (status.status === 'Completed') {
            this.stopJobTracking();
            try { sessionStorage.removeItem(this.ACTIVE_JOB_KEY); } catch {}
            this.isUniversalReview = false;
            this.parseState = 'PARSED';
            this.loadReviewTransactions(1);
            this.currentStep = 3;
            this.cdr.markForCheck();
            return;
          }

          if (status.status === 'ReviewRequired') {
            this.stopJobTracking();
            try { sessionStorage.removeItem(this.ACTIVE_JOB_KEY); } catch {}
            this.isUniversalReview = true;
            this.parseState = 'PARSED';
            this.loadUniversalReview(jobId);
            this.currentStep = 3;
            this.cdr.markForCheck();
            return;
          }

          if (status.status === 'Failed') {
            this.stopJobTracking();
            this.parseState = 'ERROR';
            this.parseError = status.errorMessage || 'Background processing failed.';
            this.cdr.markForCheck();
            return;
          }

          if (status.status === 'Cancelled') {
            this.stopJobTracking();
            this.parseState = 'IDLE';
            this.cdr.markForCheck();
            return;
          }

          // Continue polling while Queued or Processing
          this.jobPollSubscription = setTimeout(() => poll(), 1500);
          this.cdr.markForCheck();
        },
        error: (err: any) => {
          this.isPollingJob = false;
          if (err?.status === 404) {
            this.stopJobTracking();
            try { sessionStorage.removeItem(this.ACTIVE_JOB_KEY); } catch {}
          } else {
            // Retry on transient errors
            this.jobPollSubscription = setTimeout(() => poll(), 3000);
          }
          this.cdr.markForCheck();
        }
      });
    };

    poll();
  }

  stopJobTracking(): void {
    if (this.jobPollSubscription) {
      clearTimeout(this.jobPollSubscription);
      this.jobPollSubscription = null;
    }
    this.isPollingJob = false;
  }

  cancelActiveJob(): void {
    if (!this.activeJobId) return;
    this.isCancellingJob = true;
    this.cdr.markForCheck();

    this.statementService.cancelJob(this.activeJobId).subscribe({
      next: () => {
        this.isCancellingJob = false;
        this.stopJobTracking();
        if (this.jobStatus) {
          this.jobStatus.status = 'Cancelled';
          this.jobStatus.stage = 'Cancelled by user';
          this.jobStatus.isTerminal = true;
        }
        try { sessionStorage.removeItem(this.ACTIVE_JOB_KEY); } catch {}
        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.isCancellingJob = false;
        alert(err?.error?.detail || 'Failed to cancel background job.');
        this.cdr.markForCheck();
      }
    });
  }

  retryActiveJob(): void {
    if (!this.activeJobId) return;
    this.isRetryingJob = true;
    this.cdr.markForCheck();

    this.statementService.retryJob(this.activeJobId).subscribe({
      next: (status: StatementJobStatusDto) => {
        this.isRetryingJob = false;
        this.jobStatus = status;
        this.startJobTracking(this.activeJobId!);
        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.isRetryingJob = false;
        alert(err?.error?.detail || 'Failed to re-enqueue job.');
        this.cdr.markForCheck();
      }
    });
  }

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

    this.selectedFile = file;
    this.uploadState = 'FILE_SELECTED';
    this.cdr.markForCheck();
  }

  startUpload(): void {
    if (!this.selectedFile) return;

    if (this.uploadSubscription) {
      this.uploadSubscription.unsubscribe();
      this.uploadSubscription = null;
    }

    this.uploadState = 'UPLOADING';
    this.uploadProgress = 0;
    this.uploadError = null;
    this.uploadResult = null;
    this.extractionState = 'IDLE';
    this.extractionResult = null;
    this.extractionError = null;
    this.cdr.markForCheck();

    this.uploadSubscription = this.statementService.uploadStatement(this.selectedFile).subscribe({
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
            this.uploadSubscription = null;
            this.uploadProgress = 100;
            this.uploadResult = event.body;
            this.uploadState = 'UPLOADED';
            this.cdr.markForCheck();
            if (this.uploadResult?.fileId) {
              const jobId = this.uploadResult.jobId || this.uploadResult.fileId;
              this.startJobTracking(jobId);
            }
            break;
        }
      },
      error: (err: any) => {
        this.uploadSubscription = null;
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
        this.uploadSubscription = null;
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

  cancelUpload(): void {
    if (this.uploadSubscription) {
      this.uploadSubscription.unsubscribe();
      this.uploadSubscription = null;
    }
    this.uploadState = 'FILE_SELECTED';
    this.uploadProgress = 0;
    this.uploadError = null;
    this.cdr.markForCheck();
  }

  triggerExtraction(): void {
    if (!this.uploadResult?.fileId) return;

    if (this.extractionState === 'PASSWORD_REQUIRED' && !this.statementPassword) {
      this.openPasswordModal();
      return;
    }

    this.extractionState = 'EXTRACTING';
    this.extractionError = null;
    this.cdr.markForCheck();

    const extract$ = this.statementService.extractStatement(this.uploadResult.fileId, this.statementPassword || undefined);
    if (!extract$) return;

    extract$.subscribe({
      next: (result: PdfExtractionResult) => {
        this.extractionResult = result;
        this.selectedPageNumber = 1;

        if (result.extractionStatus === 'PasswordProtected') {
          this.extractionState = 'PASSWORD_REQUIRED';
          this.extractionError = result.errors?.[0] || 'This PDF is password protected. Enter the PDF password to continue.';
          this.openPasswordModal();
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
        const isPasswordProtected =
          err?.error?.requiresPassword === true ||
          err?.error?.extensions?.requiresPassword === true ||
          err?.error?.extractionStatus === 'PasswordProtected' ||
          err?.error?.title === 'Password Protected PDF' ||
          err?.error?.extensions?.extractionStatus === 'PasswordProtected' ||
          (typeof err?.error?.detail === 'string' && err.error.detail.toLowerCase().includes('password'));

        if (isPasswordProtected) {
          this.extractionState = 'PASSWORD_REQUIRED';
          this.extractionError = err?.error?.detail || 'This PDF is password protected. Enter the PDF password to continue.';
          this.openPasswordModal();
        } else {
          this.extractionState = 'ERROR';
          if (err?.error?.detail && typeof err.error.detail === 'string') {
            this.extractionError = err.error.detail;
          } else if (err?.error?.title && typeof err.error.title === 'string') {
            this.extractionError = err.error.title;
          } else {
            this.extractionError = 'PDF extraction request failed. Please check server logs.';
          }
        }
        this.cdr.markForCheck();
      }
    });
  }

  openPasswordModal(): void {
    this.showPasswordModal = true;
    this.modalPassword = '';
    this.passwordModalError = null;
    this.isSubmittingPassword = false;
    this.cdr.markForCheck();
  }

  cancelPasswordModal(): void {
    this.showPasswordModal = false;
    this.modalPassword = '';
    this.passwordModalError = null;
    this.isSubmittingPassword = false;
    this.cdr.markForCheck();
  }

  submitPasswordModal(): void {
    if (!this.modalPassword || this.isSubmittingPassword) return;

    // Background Job Processing (Phase 6): If active background job exists, unlock via unlockJob
    if (this.activeJobId) {
      this.isSubmittingPassword = true;
      this.passwordModalError = null;
      this.cdr.markForCheck();

      const enteredPassword = this.modalPassword;
      this.statementService.unlockJob(this.activeJobId, enteredPassword).subscribe({
        next: (status: StatementJobStatusDto) => {
          this.showPasswordModal = false;
          this.modalPassword = '';
          this.statementPassword = '';
          this.passwordModalError = null;
          this.isSubmittingPassword = false;
          this.jobStatus = status;
          this.startJobTracking(this.activeJobId!);
          this.cdr.markForCheck();
        },
        error: (err: any) => {
          this.isSubmittingPassword = false;
          this.modalPassword = '';
          const isWrong = err?.status === 400 || (typeof err?.error?.detail === 'string' && err.error.detail.toLowerCase().includes('password'));
          this.passwordModalError = isWrong
            ? 'Incorrect PDF password. Please try again.'
            : (err?.error?.detail || err?.error?.title || 'Failed to unlock PDF statement. Please try again.');
          this.cdr.markForCheck();
        }
      });
      return;
    }

    if (!this.uploadResult?.fileId) return;

    this.isSubmittingPassword = true;
    this.passwordModalError = null;
    this.cdr.markForCheck();

    const enteredPassword = this.modalPassword;
    const extract$ = this.statementService.extractStatement(this.uploadResult.fileId, enteredPassword);
    if (!extract$) {
      this.isSubmittingPassword = false;
      return;
    }

    extract$.subscribe({
      next: (result: PdfExtractionResult) => {
        if (result.extractionStatus === 'PasswordProtected') {
          this.passwordModalError = 'Incorrect PDF password. Please try again.';
          this.modalPassword = '';
          this.isSubmittingPassword = false;
          this.cdr.markForCheck();
          return;
        }

        // Successfully unlocked: zero password persistence
        this.showPasswordModal = false;
        this.modalPassword = '';
        this.statementPassword = '';
        this.passwordModalError = null;
        this.isSubmittingPassword = false;

        this.extractionResult = result;
        this.selectedPageNumber = 1;
        if (result.extractionStatus === 'NoDigitalTextDetected' || !result.hasUsableText) {
          this.extractionState = 'NO_TEXT';
          this.currentStep = 2;
        } else if (result.extractionStatus === 'Failed') {
          this.extractionState = 'ERROR';
          this.extractionError = result.errors?.[0] || 'PDF structure extraction failed.';
        } else {
          this.extractionState = 'EXTRACTED';
          this.currentStep = 2;
        }
        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.isSubmittingPassword = false;
        this.modalPassword = '';

        const isWrongPassword =
          err?.error?.isIncorrectPassword === true ||
          err?.error?.extensions?.isIncorrectPassword === true ||
          err?.error?.requiresPassword === true ||
          err?.error?.extensions?.requiresPassword === true ||
          err?.error?.extractionStatus === 'PasswordProtected' ||
          err?.error?.title === 'Invalid PDF Password' ||
          err?.error?.title === 'Password Protected PDF' ||
          err?.error?.extensions?.extractionStatus === 'PasswordProtected' ||
          (typeof err?.error?.detail === 'string' && err.error.detail.toLowerCase().includes('password'));

        if (isWrongPassword) {
          this.passwordModalError = 'Incorrect PDF password. Please try again.';
        } else if (err?.error?.detail && typeof err.error.detail === 'string') {
          this.passwordModalError = err.error.detail;
        } else if (err?.error?.title && typeof err.error.title === 'string') {
          this.passwordModalError = err.error.title;
        } else {
          this.passwordModalError = 'Failed to unlock PDF statement. Please try again.';
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

    if (this.extractionState === 'PASSWORD_REQUIRED') {
      this.openPasswordModal();
      return;
    }

    this.parseState = 'PARSING';
    this.parseError = null;
    this.cdr.markForCheck();

    this.statementService.parseStatement(this.uploadResult.fileId).subscribe({
      next: (res: any) => {
        this.parseResult = res;
        this.parseState = 'PARSED';
        if (res?.bankCode === 99 || (res?.parserVersion && res.parserVersion.includes('Universal'))) {
          this.isUniversalReview = true;
          this.loadUniversalReview(this.uploadResult!.fileId);
        } else {
          this.isUniversalReview = false;
          this.loadReviewTransactions(1);
        }
        this.currentStep = 3;
        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.parseState = 'ERROR';
        const isPasswordRelated =
          (typeof err?.error?.detail === 'string' && (err.error.detail.toLowerCase().includes('password') || err.error.detail.toLowerCase().includes('encrypted')));
        if (isPasswordRelated) {
          this.extractionState = 'PASSWORD_REQUIRED';
          this.openPasswordModal();
        } else if (err?.error?.detail) {
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
    if (this.uploadSubscription) {
      this.uploadSubscription.unsubscribe();
      this.uploadSubscription = null;
    }
    this.stopJobTracking();
    this.activeJobId = null;
    this.jobStatus = null;
    try { sessionStorage.removeItem(this.ACTIVE_JOB_KEY); } catch {}
    this.selectedFile = null;
    this.uploadState = 'IDLE';
    this.uploadProgress = 0;
    this.uploadError = null;
    this.uploadResult = null;
    this.extractionState = 'IDLE';
    this.extractionResult = null;
    this.extractionError = null;
    this.statementPassword = '';
    this.showPasswordModal = false;
    this.modalPassword = '';
    this.passwordModalError = null;
    this.isSubmittingPassword = false;
    this.selectedPageNumber = 1;
    this.selectedInspectionTab = 'summary';
    this.parseState = 'IDLE';
    this.parseResult = null;
    this.parseError = null;
    this.reviewTransactions = [];
    this.validationSummary = null;
    this.currentStep = 1;
    this.isEditingTransaction = false;
    this.editingTransaction = null;
    this.successNotice = null;
    this.isExportingExcel = false;
    this.exportError = null;
    this.cdr.markForCheck();
  }

  ngOnDestroy(): void {
    if (this.uploadSubscription) {
      this.uploadSubscription.unsubscribe();
      this.uploadSubscription = null;
    }
    this.stopJobTracking();
  }

  setStep(step: number): void {
    this.currentStep = step;
  }

  formatDate(dateVal?: string | null): string {
    if (!dateVal) return '-';
    try {
      if (/^\d{2}\/\d{2}\/\d{4}/.test(dateVal)) return dateVal.substring(0, 10);
      const d = new Date(dateVal);
      if (isNaN(d.getTime())) return dateVal;
      const day = String(d.getDate()).padStart(2, '0');
      const month = String(d.getMonth() + 1).padStart(2, '0');
      const year = d.getFullYear();
      return `${day}/${month}/${year}`;
    } catch {
      return dateVal || '-';
    }
  }

  downloadExcel(): void {
    if (!this.uploadResult?.fileId) {
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

    this.statementService.exportExcel(this.uploadResult.fileId).pipe(
      timeout({
        each: 15000,
        with: () => throwError(() => new Error('Excel generation request timed out. You can export verified transactions directly as CSV below.'))
      })
    ).subscribe({
      next: (blob: Blob) => {
        this.isExportingExcel = false;
        const bankName = (this.validationSummary?.bankName || 'Statement').replace(/[^a-zA-Z0-9_-]/g, '_');
        const dateStr = new Date().toISOString().substring(0, 10).replace(/-/g, '');
        const filename = `Accufex_${bankName}_${dateStr}.xlsx`;

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
        let msg = 'Failed to export statement to Excel.';
        if (err?.error instanceof Blob) {
          try {
            const errorText = await err.error.text();
            try {
              const json = JSON.parse(errorText);
              msg = json?.detail || json?.title || msg;
            } catch {
              if (errorText.includes('Packaging') || errorText.includes('FileNotFoundException')) {
                msg = 'Backend Excel engine dependency missing. You can export verified transactions directly as CSV below.';
              } else {
                msg = 'Server Excel export encountered an error. You can export verified transactions as CSV below.';
              }
            }
          } catch {
            msg = 'Failed to export statement to Excel.';
          }
        } else {
          msg = err?.error?.detail || err?.error?.title || err?.message || msg;
        }
        this.exportError = msg;
        this.cdr.markForCheck();
      }
    });
  }

  exportTransactionsAsCsv(): void {
    const list = this.reviewTransactions.length > 0 ? this.reviewTransactions : [];
    if (list.length === 0) {
      return;
    }
    const headers = ['Sr No', 'Transaction Date', 'Value Date', 'Particulars / Description', 'Cheque / Ref', 'Debit (INR)', 'Credit (INR)', 'Balance (INR)', 'Validation Status'];
    const rows = list.map((t, idx) => [
      idx + 1,
      this.formatDate(t.transactionDate),
      this.formatDate(t.valueDate),
      `"${(t.description || '').replace(/"/g, '""')}"`,
      t.reference || t.utr || '',
      t.debit !== null && t.debit !== undefined ? t.debit.toFixed(2) : '',
      t.credit !== null && t.credit !== undefined ? t.credit.toFixed(2) : '',
      t.balance !== null && t.balance !== undefined ? t.balance.toFixed(2) : '',
      t.validationStatus || 'VALID'
    ]);

    const csvContent = [headers.join(','), ...rows.map(r => r.join(','))].join('\r\n');
    const blob = new Blob([csvContent], { type: 'text/csv;charset=utf-8;' });
    const bankName = (this.validationSummary?.bankName || 'Statement').replace(/[^a-zA-Z0-9_-]/g, '_');
    const filename = `Accufex_${bankName}_Ledger.csv`;
    const link = document.createElement('a');
    link.href = URL.createObjectURL(blob);
    link.setAttribute('download', filename);
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    setTimeout(() => URL.revokeObjectURL(link.href), 1000);
  }

  // =========================================================================
  // 5-Stage Institutional Workflow Stepper Getters (Phase 8)
  // =========================================================================
  get step1Status(): 'completed' | 'active' | 'pending' | 'error' {
    if (this.uploadState === 'ERROR') return 'error';
    if (this.currentStep > 1 || this.uploadState === 'UPLOADED' || this.uploadResult !== null) return 'completed';
    return 'active';
  }

  get step2Status(): 'completed' | 'active' | 'pending' | 'error' {
    if (this.jobStatus?.status === 'Failed' || this.parseState === 'ERROR' || this.extractionState === 'ERROR' || this.extractionState === 'PASSWORD_REQUIRED') return 'error';
    if (this.currentStep > 2 || this.jobStatus?.status === 'Completed' || this.parseState === 'PARSED' || this.reviewTransactions.length > 0) return 'completed';
    if (this.currentStep === 2 || this.jobStatus?.status === 'Processing' || this.jobStatus?.status === 'Queued' || this.extractionState === 'EXTRACTING' || this.parseState === 'PARSING' || this.uploadState === 'UPLOADED') return 'active';
    return 'pending';
  }

  get step3Status(): 'completed' | 'active' | 'pending' | 'error' {
    if (this.validationSummary && this.validationSummary.invalidCount > 0) return 'error';
    if (this.currentStep > 3 || (this.validationSummary && this.validationSummary.invalidCount === 0)) return 'completed';
    if (this.currentStep === 3) return 'active';
    return 'pending';
  }

  get step4Status(): 'completed' | 'active' | 'pending' | 'error' {
    if (this.validationSummary && this.validationSummary.invalidCount > 0) return 'error';
    if (this.currentStep > 3) return 'completed';
    if (this.currentStep === 3 && (this.reviewTransactions.length > 0 || this.validationSummary !== null)) return 'active';
    return 'pending';
  }

  get step5Status(): 'completed' | 'active' | 'pending' | 'error' {
    if (this.exportError) return 'error';
    if (this.validationSummary && this.validationSummary.invalidCount > 0) return 'error';
    if (this.successNotice && this.successNotice.toLowerCase().includes('excel')) return 'completed';
    if (this.currentStep === 4 || this.isExportingExcel) return 'active';
    return 'pending';
  }

  onStepperClick(stepNumber: number): void {
    if (stepNumber === 1) {
      this.setStep(1);
    } else if (stepNumber === 2) {
      if (this.uploadResult || this.extractionResult || this.jobStatus) {
        this.setStep(2);
      }
    } else if (stepNumber === 3 || stepNumber === 4) {
      if (this.parseState === 'PARSED' || this.reviewTransactions.length > 0 || this.isUniversalReview || this.jobStatus?.status === 'Completed' || this.jobStatus?.status === 'ReviewRequired') {
        this.setStep(3);
      }
    } else if (stepNumber === 5) {
      if (this.parseState === 'PARSED' || this.reviewTransactions.length > 0 || this.jobStatus?.status === 'Completed') {
        this.setStep(4);
      }
    }
  }

  get detectedBankName(): string {
    return this.jobStatus?.detectedBankName || this.validationSummary?.bankName || this.parseResult?.bankName || '';
  }

  get totalTransactionCount(): number {
    return this.jobStatus?.transactionCount || this.validationSummary?.totalTransactions || this.reviewTotalCount || 0;
  }

  get validationHealthStatus(): 'ready' | 'review' | 'blocked' | 'idle' {
    if (!this.validationSummary) {
      return 'idle';
    }
    if (this.validationSummary.invalidCount > 0) return 'blocked';
    if (this.validationSummary.reviewCount > 0) return 'review';
    return 'ready';
  }

  get isExportBlocked(): boolean {
    return !!(this.validationSummary && this.validationSummary.invalidCount > 0);
  }

  // =========================================================================
  // UNIVERSAL REVIEW WORKBENCH METHODS (Phase 3)
  // =========================================================================
  loadUniversalReview(fileId: string): void {
    this.isLoadingUniversalReview = true;
    this.universalReviewError = null;
    this.cdr.markForCheck();

    this.statementService.getUniversalReview(fileId).subscribe({
      next: (review: UniversalReviewDto) => {
        this.universalReview = review;
        this.isUniversalReview = true;
        this.isLoadingUniversalReview = false;
        this.currentStep = 3;
        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.isLoadingUniversalReview = false;
        this.universalReviewError = err?.error?.detail || err?.error?.title || 'Failed to load universal review session.';
        this.cdr.markForCheck();
      }
    });
  }

  openEditCandidate(candidate: UniversalCandidateTransactionDto): void {
    this.editingCandidate = candidate;
    this.candidateEditForm = {
      date: candidate.date ? candidate.date.substring(0, 10) : '',
      valueDate: candidate.valueDate ? candidate.valueDate.substring(0, 10) : '',
      description: candidate.description,
      reference: candidate.reference || '',
      debit: candidate.debit || null,
      credit: candidate.credit || null,
      balance: candidate.balance || null,
      reason: ''
    };
    this.candidateEditError = null;
    this.isEditingCandidate = true;
    this.cdr.markForCheck();
  }

  closeEditCandidate(): void {
    this.isEditingCandidate = false;
    this.editingCandidate = null;
    this.candidateEditError = null;
    this.cdr.markForCheck();
  }

  saveCandidateEdit(): void {
    if (!this.editingCandidate || !this.uploadResult?.fileId) return;

    if (!this.candidateEditForm.description || !this.candidateEditForm.description.trim()) {
      this.candidateEditError = 'Description cannot be empty.';
      return;
    }

    if (!this.candidateEditForm.date) {
      this.candidateEditError = 'A valid transaction date is required.';
      return;
    }

    const debit = this.candidateEditForm.debit;
    const credit = this.candidateEditForm.credit;

    if (debit && debit > 0 && credit && credit > 0) {
      this.candidateEditError = 'Both Debit and Credit cannot be entered simultaneously.';
      return;
    }

    if ((!debit || debit <= 0) && (!credit || credit <= 0)) {
      this.candidateEditError = 'Either Debit or Credit must be greater than zero.';
      return;
    }

    this.isSavingCandidate = true;
    this.candidateEditError = null;
    this.cdr.markForCheck();

    const request: CorrectUniversalTransactionRequest = {
      transactionDate: this.candidateEditForm.date,
      valueDate: this.candidateEditForm.valueDate || this.candidateEditForm.date,
      description: this.candidateEditForm.description.trim(),
      reference: this.candidateEditForm.reference ? this.candidateEditForm.reference.trim() : null,
      debit: debit && debit > 0 ? debit : null,
      credit: credit && credit > 0 ? credit : null,
      balance: this.candidateEditForm.balance,
      reason: this.candidateEditForm.reason
    };

    this.statementService.correctUniversalTransaction(this.uploadResult.fileId, this.editingCandidate.id, request).subscribe({
      next: (updatedReview: UniversalReviewDto) => {
        this.universalReview = updatedReview;
        this.isSavingCandidate = false;
        this.closeEditCandidate();
        this.successNotice = 'Candidate transaction updated and revalidated successfully.';
        setTimeout(() => { this.successNotice = null; this.cdr.markForCheck(); }, 4000);
        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.isSavingCandidate = false;
        this.candidateEditError = err?.error?.detail || err?.error?.title || 'Failed to update candidate transaction.';
        this.cdr.markForCheck();
      }
    });
  }

  approveAndConvert(): void {
    if (!this.uploadResult?.fileId) return;

    this.isApprovingReview = true;
    this.approvalError = null;
    this.cdr.markForCheck();

    this.statementService.approveUniversalReview(this.uploadResult.fileId).subscribe({
      next: (res: ApproveUniversalReviewResponse) => {
        this.isApprovingReview = false;
        this.isUniversalReview = false;
        this.universalReview = null;
        this.successNotice = `Statement approved! ${res.convertedCount} transaction(s) converted to Excel workbook ready for export.`;
        setTimeout(() => { this.successNotice = null; this.cdr.markForCheck(); }, 5000);
        this.loadReviewTransactions(1);
        this.currentStep = 3;
        this.cdr.markForCheck();
      },
      error: (err: any) => {
        this.isApprovingReview = false;
        this.approvalError = err?.error?.detail || err?.error?.title || 'Approval failed. Please verify that all transactions have valid dates and amounts.';
        this.cdr.markForCheck();
      }
    });
  }
}
