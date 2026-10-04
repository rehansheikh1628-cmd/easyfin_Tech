import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { ConverterComponent } from './converter.component';
import { StatementService, StatementUploadResponse, UniversalReviewDto } from '../../services/statement.service';
import { of, from, throwError, NEVER } from 'rxjs';
import { HttpEvent, HttpResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { vi, describe, it, expect, beforeEach } from 'vitest';

describe('ConverterComponent (Phase 2 & Phase 3)', () => {
  let component: ConverterComponent;
  let fixture: ComponentFixture<ConverterComponent>;
  let mockStatementService: {
    uploadStatement: ReturnType<typeof vi.fn>;
    formatBytes: (bytes: number) => string;
    extractStatement: ReturnType<typeof vi.fn>;
    getStatementExtraction: ReturnType<typeof vi.fn>;
    parseStatement: ReturnType<typeof vi.fn>;
    getTransactions: ReturnType<typeof vi.fn>;
    getTransactionById: ReturnType<typeof vi.fn>;
    correctTransaction: ReturnType<typeof vi.fn>;
    getValidationSummary: ReturnType<typeof vi.fn>;
    revalidateStatement: ReturnType<typeof vi.fn>;
    exportExcel: ReturnType<typeof vi.fn>;
    getJobStatus: ReturnType<typeof vi.fn>;
    cancelJob: ReturnType<typeof vi.fn>;
    retryJob: ReturnType<typeof vi.fn>;
    unlockJob: ReturnType<typeof vi.fn>;
    getUniversalReview: ReturnType<typeof vi.fn>;
    correctUniversalTransaction: ReturnType<typeof vi.fn>;
    updateUniversalColumns: ReturnType<typeof vi.fn>;
    approveUniversalReview: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    mockStatementService = {
      uploadStatement: vi.fn(),
      formatBytes: (bytes: number) => `${bytes} B`,
      extractStatement: vi.fn(),
      getStatementExtraction: vi.fn(),
      parseStatement: vi.fn(),
      getTransactions: vi.fn().mockReturnValue(of({ transactions: [], totalCount: 0, page: 1, pageSize: 50 })),
      getTransactionById: vi.fn(),
      correctTransaction: vi.fn(),
      getValidationSummary: vi.fn(),
      revalidateStatement: vi.fn(),
      exportExcel: vi.fn(),
      getJobStatus: vi.fn().mockReturnValue(of({
        jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        fileName: 'test.pdf',
        status: 'Queued',
        statusCode: 0,
        stage: 'Queued in pool',
        progressPercent: 10,
        isTerminal: false,
        requiresPassword: false
      })),
      cancelJob: vi.fn().mockReturnValue(of({ message: 'Cancelled', jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8' })),
      retryJob: vi.fn().mockReturnValue(of({
        jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        fileName: 'test.pdf',
        status: 'Queued',
        statusCode: 0,
        stage: 'Re-enqueued',
        progressPercent: 5,
        isTerminal: false,
        requiresPassword: false
      })),
      unlockJob: vi.fn().mockReturnValue(of({
        jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        fileName: 'test.pdf',
        status: 'Queued',
        statusCode: 0,
        stage: 'Unlocked and enqueued',
        progressPercent: 15,
        isTerminal: false,
        requiresPassword: false
      })),
      getUniversalReview: vi.fn(),
      correctUniversalTransaction: vi.fn(),
      updateUniversalColumns: vi.fn(),
      approveUniversalReview: vi.fn()
    };

    await TestBed.configureTestingModule({
      declarations: [ConverterComponent],
      imports: [CommonModule, FormsModule, RouterModule.forRoot([])],
      providers: [
        { provide: StatementService, useValue: mockStatementService }
      ]
    }).compileComponents();
  });

  beforeEach(() => {
    sessionStorage.clear();
    fixture = TestBed.createComponent(ConverterComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should initialize with IDLE upload and extraction state', () => {
    expect(component).toBeTruthy();
    expect(component.uploadState).toBe('IDLE');
    expect(component.extractionState).toBe('IDLE');
    expect(component.selectedFile).toBeNull();
    expect(component.uploadProgress).toBe(0);
    expect(component.uploadError).toBeNull();
    expect(component.extractionResult).toBeNull();
    expect(component.extractionError).toBeNull();
  });

  it('should accept a valid PDF file and set state to FILE_SELECTED', () => {
    const validFile = new File(['%PDF-1.4 sample content'], 'bank_statement.pdf', { type: 'application/pdf' });

    component.handleFileSelection(validFile);

    expect(component.uploadState).toBe('FILE_SELECTED');
    expect(component.selectedFile).toBe(validFile);
    expect(component.uploadError).toBeNull();
  });

  it('should reject a non-PDF file and set state to ERROR', () => {
    const txtFile = new File(['hello world'], 'document.txt', { type: 'text/plain' });

    component.handleFileSelection(txtFile);

    expect(component.uploadState).toBe('ERROR');
    expect(component.selectedFile).toBeNull();
    expect(component.uploadError).toContain('Please select a valid PDF file (.pdf)');
  });

  it('should reject an empty 0-byte file and set state to ERROR', () => {
    const emptyFile = new File([], 'empty.pdf', { type: 'application/pdf' });

    component.handleFileSelection(emptyFile);

    expect(component.uploadState).toBe('ERROR');
    expect(component.selectedFile).toBeNull();
    expect(component.uploadError).toContain('The selected file is empty');
  });

  it('should not reject a file larger than 50MB with a client-side limit', () => {
    const largeFile = new File(['data'], 'large_statement.pdf', { type: 'application/pdf' });
    Object.defineProperty(largeFile, 'size', { value: 55 * 1024 * 1024 });

    component.handleFileSelection(largeFile);

    expect(component.uploadState).toBe('FILE_SELECTED');
    expect(component.selectedFile).toBe(largeFile);
    expect(component.uploadError).toBeNull();
  });

  it('should transition to UPLOADING and then UPLOADED on successful server upload', () => {
    const validFile = new File(['%PDF-1.4 test'], 'august_statement.pdf', { type: 'application/pdf' });
    component.handleFileSelection(validFile);

    const mockResponse: StatementUploadResponse = {
      success: true,
      message: 'Bank statement uploaded successfully.',
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      originalFileName: 'august_statement.pdf',
      storedFileName: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8.pdf',
      fileSizeBytes: 1024,
      fileSizeFormatted: '1.00 KB',
      contentType: 'application/pdf',
      uploadedAt: new Date().toISOString(),
      status: 'ReadyForProcessing',
      processingStatus: 0,
      isDuplicate: false,
      fileHash: 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
      clientId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      financialYearId: '3EBC9536-20BA-4624-BCC4-584122C3BCAA'
    };

    const httpResponse = new HttpResponse<StatementUploadResponse>({
      body: mockResponse,
      status: 200
    });

    mockStatementService.uploadStatement.mockReturnValue(of(httpResponse as HttpEvent<StatementUploadResponse>));

    component.startUpload();

    expect(component.uploadState).toBe('UPLOADED');
    expect(component.uploadProgress).toBe(100);
    expect(component.uploadResult).toEqual(mockResponse);
    expect(component.uploadError).toBeNull();
  });

  it('should handle Sent and UploadProgress events and transition to UPLOADED on Response', () => {
    const validFile = new File(['%PDF-1.4 test'], 'august_statement.pdf', { type: 'application/pdf' });
    component.handleFileSelection(validFile);

    const mockResponse: StatementUploadResponse = {
      success: true,
      message: 'Bank statement uploaded successfully.',
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      originalFileName: 'august_statement.pdf',
      storedFileName: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8.pdf',
      fileSizeBytes: 1024,
      fileSizeFormatted: '1.00 KB',
      contentType: 'application/pdf',
      uploadedAt: new Date().toISOString(),
      status: 'ReadyForProcessing',
      processingStatus: 0,
      isDuplicate: false,
      fileHash: 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
      clientId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      financialYearId: '3EBC9536-20BA-4624-BCC4-584122C3BCAA'
    };

    const uploadEvents$ = from<HttpEvent<StatementUploadResponse>[]>([
      { type: 0 }, // Sent
      { type: 1, loaded: 500, total: 1000 }, // UploadProgress 50%
      new HttpResponse<StatementUploadResponse>({ body: mockResponse, status: 200 })
    ]);

    mockStatementService.uploadStatement.mockReturnValue(uploadEvents$);

    component.startUpload();

    expect(component.uploadState).toBe('UPLOADED');
    expect(component.uploadProgress).toBe(100);
    expect(component.uploadResult).toEqual(mockResponse);
  });

  it('should transition to ERROR on network failure (status 0) and not stay in UPLOADING', () => {
    const validFile = new File(['%PDF-1.4 test'], 'statement.pdf', { type: 'application/pdf' });
    component.handleFileSelection(validFile);

    mockStatementService.uploadStatement.mockReturnValue(
      throwError(() => ({ status: 0 }))
    );

    component.startUpload();

    expect(component.uploadState).toBe('ERROR');
    expect(component.uploadError).toContain('Unable to connect to backend server');
  });

  it('should trigger extraction and transition to EXTRACTED on success', () => {
    component.uploadResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8'
    } as any;

    const mockExtractionResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      originalFileName: 'statement.pdf',
      pageCount: 1,
      extractionStatus: 'DigitalTextExtracted',
      hasUsableText: true,
      wordCount: 25,
      characterCount: 150,
      textBlockCount: 25,
      candidateRowCount: 5,
      candidateTableCount: 1,
      pages: [
        {
          pageNumber: 1,
          width: 612,
          height: 792,
          rawText: 'Statement content',
          wordCount: 25,
          characterCount: 150,
          hasUsableText: true,
          textBlocks: [],
          candidateRows: [],
          candidateTables: [],
          repeatedHeaders: [],
          repeatedFooters: []
        }
      ],
      warnings: [],
      errors: []
    };

    mockStatementService.extractStatement.mockReturnValue(of(mockExtractionResult));

    component.triggerExtraction();

    expect(mockStatementService.extractStatement).toHaveBeenCalledWith('9A7E688C-841D-4C64-9D0C-8B988CCD63F8', undefined);
    expect(component.extractionState).toBe('EXTRACTED');
    expect(component.extractionResult).toEqual(mockExtractionResult as any);
    expect(component.currentStep).toBe(2);
  });

  it('should handle NoDigitalTextDetected honest status without failing whole process', () => {
    component.uploadResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8'
    } as any;

    const mockExtractionResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      originalFileName: 'scanned.pdf',
      pageCount: 1,
      extractionStatus: 'NoDigitalTextDetected',
      hasUsableText: false,
      wordCount: 0,
      characterCount: 0,
      textBlockCount: 0,
      candidateRowCount: 0,
      candidateTableCount: 0,
      pages: [],
      warnings: ['OCR required in a future phase.'],
      errors: []
    };

    mockStatementService.extractStatement.mockReturnValue(of(mockExtractionResult));

    component.triggerExtraction();

    expect(component.extractionState).toBe('NO_TEXT');
    expect(component.currentStep).toBe(2);
  });

  it('should verify Statement Options, Bank Layout dropdown, and permanent password input are completely removed', () => {
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;

    // Statement Options card must be completely absent
    expect(compiled.querySelector('.upload-options-card')).toBeNull();
    expect(compiled.querySelector('#bankSelect')).toBeNull();
    expect(compiled.querySelector('#pdfPassword')).toBeNull();
    expect(compiled.textContent).not.toContain('Statement Options');
    expect(compiled.textContent).not.toContain('CONFIG');
    expect(compiled.textContent).not.toContain('Bank Layout Detection');
    expect(compiled.textContent).not.toContain('Standard Visual Bank Layout');
    expect(compiled.textContent).not.toContain('Engine Architecture');
    expect(compiled.textContent).not.toContain('ClosedXML OpenXML (.xlsx)');
  });

  it('should process unencrypted PDF automatically without showing password modal', () => {
    component.uploadResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8'
    } as any;

    const mockExtractionResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      pageCount: 2,
      extractionStatus: 'DigitalTextExtracted',
      hasUsableText: true,
      pages: []
    };

    mockStatementService.extractStatement.mockReturnValue(of(mockExtractionResult));

    component.triggerExtraction();
    fixture.detectChanges();

    expect(component.extractionState).toBe('EXTRACTED');
    expect(component.showPasswordModal).toBe(false);
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.password-modal-backdrop')).toBeNull();
  });

  it('should pause and show professional password modal when PDF is password-protected', () => {
    component.uploadResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8'
    } as any;

    const mockExtractionResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      originalFileName: 'protected.pdf',
      pageCount: 0,
      extractionStatus: 'PasswordProtected',
      hasUsableText: false,
      wordCount: 0,
      characterCount: 0,
      textBlockCount: 0,
      candidateRowCount: 0,
      candidateTableCount: 0,
      pages: [],
      warnings: [],
      errors: ['Document is encrypted and requires a password.']
    };

    mockStatementService.extractStatement.mockReturnValue(of(mockExtractionResult));

    component.triggerExtraction();
    fixture.detectChanges();

    expect(component.extractionState).toBe('PASSWORD_REQUIRED');
    expect(component.showPasswordModal).toBe(true);

    const compiled = fixture.nativeElement as HTMLElement;
    const modalBackdrop = compiled.querySelector('.password-modal-backdrop');
    expect(modalBackdrop).not.toBeNull();

    const heading = compiled.querySelector('#pwdModalTitle');
    expect(heading?.textContent?.trim()).toBe('This PDF is password protected.');

    const subheading = compiled.querySelector('.password-modal-subheading');
    expect(subheading?.textContent?.trim()).toBe('Enter the PDF password to continue.');

    const passwordInput = compiled.querySelector('#modalPdfPassword') as HTMLInputElement;
    expect(passwordInput).not.toBeNull();
    expect(passwordInput.type).toBe('password');
  });

  it('should show clear error when incorrect password is submitted in modal and keep modal open', () => {
    component.uploadResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8'
    } as any;
    component.showPasswordModal = true;
    component.modalPassword = 'wrong_password';

    // Mock server returning 400 with PasswordProtected title and extensions
    mockStatementService.extractStatement.mockReturnValue(
      throwError(() => ({
        status: 400,
        error: {
          title: 'Invalid PDF Password',
          detail: 'Incorrect PDF password. Please try again.',
          requiresPassword: true,
          extractionStatus: 'PasswordProtected',
          isIncorrectPassword: true
        }
      }))
    );

    component.submitPasswordModal();
    fixture.detectChanges();

    expect(component.showPasswordModal).toBe(true);
    expect(component.passwordModalError).toBe('Incorrect PDF password. Please try again.');
    expect(component.modalPassword).toBe(''); // Cleared for security

    const compiled = fixture.nativeElement as HTMLElement;
    const errorAlert = compiled.querySelector('.modal-error-alert');
    expect(errorAlert?.textContent).toContain('Incorrect PDF password. Please try again.');
  });

  it('should unlock successfully with correct password, continue workflow, and clear client-side password', () => {
    component.uploadResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8'
    } as any;
    component.showPasswordModal = true;
    component.modalPassword = 'correct_password_123';

    const mockUnlockedResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      originalFileName: 'protected.pdf',
      pageCount: 3,
      extractionStatus: 'DigitalTextExtracted',
      hasUsableText: true,
      pages: [{ pageNumber: 1, rawText: 'Statement content' }]
    };

    mockStatementService.extractStatement.mockReturnValue(of(mockUnlockedResult));

    component.submitPasswordModal();
    fixture.detectChanges();

    expect(mockStatementService.extractStatement).toHaveBeenCalledWith('9A7E688C-841D-4C64-9D0C-8B988CCD63F8', 'correct_password_123');
    expect(component.showPasswordModal).toBe(false);
    expect(component.extractionState).toBe('EXTRACTED');
    expect(component.currentStep).toBe(2);

    // SECURITY CHECK: Password must be completely wiped from memory
    expect(component.modalPassword).toBe('');
    expect(component.statementPassword).toBe('');
    expect(component.passwordModalError).toBeNull();
  });

  it('should cancel password modal cleanly and clear state', () => {
    component.showPasswordModal = true;
    component.modalPassword = 'partially_typed';
    component.passwordModalError = 'Previous error';

    component.cancelPasswordModal();
    fixture.detectChanges();

    expect(component.showPasswordModal).toBe(false);
    expect(component.modalPassword).toBe('');
    expect(component.passwordModalError).toBeNull();
  });

  it('should switch inspection pages and tabs correctly', () => {
    component.extractionResult = {
      pages: [
        { pageNumber: 1, rawText: 'Page 1' },
        { pageNumber: 2, rawText: 'Page 2' }
      ]
    } as any;

    expect(component.selectedPageNumber).toBe(1);
    expect(component.selectedPageResult?.rawText).toBe('Page 1');

    component.selectInspectionPage(2);
    expect(component.selectedPageNumber).toBe(2);
    expect(component.selectedPageResult?.rawText).toBe('Page 2');

    component.setInspectionTab('rawText');
    expect(component.selectedInspectionTab).toBe('rawText');
  });

  it('should verify the entire page-analysis section is completely removed from Step 2 UI', () => {
    component.currentStep = 2;
    component.extractionResult = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      originalFileName: 'test_statement.pdf',
      pageCount: 3,
      extractionStatus: 'DigitalTextExtracted',
      hasUsableText: true,
      wordCount: 120,
      characterCount: 850,
      textBlockCount: 45,
      candidateRowCount: 12,
      candidateTableCount: 1,
      durationMs: 34,
      pages: [
        {
          pageNumber: 1,
          width: 612,
          height: 792,
          rawText: 'Raw extracted text from page 1',
          wordCount: 40,
          characterCount: 280,
          hasUsableText: true,
          textBlocks: [],
          candidateRows: [],
          candidateTables: [],
          repeatedHeaders: [],
          repeatedFooters: []
        }
      ],
      warnings: [],
      errors: []
    } as any;
    component.uploadResult = { fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8' } as any;
    mockStatementService.extractStatement.mockReturnValue(of(component.extractionResult));
    component.triggerExtraction();
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;

    // The overview card and actions must remain intact
    expect(compiled.querySelector('.inspection-overview-card')).not.toBeNull();
    expect(compiled.textContent).toContain('Document Visual Geometry Inspection');
    expect(compiled.textContent).toContain('Back to Upload');
    expect(compiled.textContent).toContain('Parse Transactions & Review');

    // The entire page-analysis section and all its sub-elements must be completely absent from DOM
    expect(compiled.querySelector('.inspection-detail-card')).toBeNull();
    expect(compiled.querySelector('.page-nav-bar')).toBeNull();
    expect(compiled.querySelector('.page-pills')).toBeNull();
    expect(compiled.querySelector('.page-dim-badge')).toBeNull();
    expect(compiled.querySelector('.inspection-tab-bar')).toBeNull();
    expect(compiled.querySelector('.raw-text-toolbar')).toBeNull();
    expect(compiled.querySelector('.raw-text-box')).toBeNull();
    expect(compiled.querySelector('.candidate-rows-list')).toBeNull();
    expect(compiled.querySelector('.candidate-table-card')).toBeNull();

    // Specific text strings must not be rendered anywhere in this step
    const renderedText = compiled.textContent || '';
    expect(renderedText).not.toContain('Select Page:');
    expect(renderedText).not.toContain('Page Overview');
    expect(renderedText).not.toContain('Raw Page Text');
    expect(renderedText).not.toContain('Text Blocks & Coordinates');
    expect(renderedText).not.toContain('Page Header Diagnostics');
    expect(renderedText).not.toContain('Page Footer Diagnostics');
    expect(renderedText).not.toContain('Page Text Preview');
    expect(renderedText).not.toContain('View Full Raw Text');
    expect(renderedText).not.toContain('Raw extracted text from page 1');

    // Technical processing-details badges and separators must be removed
    expect(compiled.querySelectorAll('.meta-dot').length).toBe(0);
    expect(renderedText).not.toContain('Duration:');
    expect(renderedText).not.toContain('Deterministic Visual Line Grouping');
    expect(renderedText).not.toContain('Coords: Top-Left');

    // Retained elements: status badge, filename, action buttons, statistics
    expect(renderedText).toContain('DigitalTextExtracted');
    expect(renderedText).toContain('test_statement.pdf');
    expect(renderedText).toContain('Pages');
    expect(renderedText).toContain('Words');
    expect(renderedText).toContain('Characters');
    expect(renderedText).toContain('Text Blocks');
    expect(renderedText).toContain('Candidate Rows');
    expect(renderedText).toContain('Candidate Tables');
  });

  it('should reset state completely when resetUpload is called', () => {
    component.selectedFile = new File(['%PDF'], 'test.pdf');
    component.uploadState = 'UPLOADED';
    component.uploadProgress = 100;
    component.uploadError = 'some error';
    component.extractionState = 'EXTRACTED';
    component.extractionResult = { pageCount: 1 } as any;
    component.statementPassword = 'secret';
    component.showPasswordModal = true;
    component.modalPassword = 'secret_modal';

    component.resetUpload();

    expect(component.selectedFile).toBeNull();
    expect(component.uploadState).toBe('IDLE');
    expect(component.uploadProgress).toBe(0);
    expect(component.uploadError).toBeNull();
    expect(component.uploadResult).toBeNull();
    expect(component.extractionState).toBe('IDLE');
    expect(component.extractionResult).toBeNull();
    expect(component.statementPassword).toBe('');
    expect(component.showPasswordModal).toBe(false);
    expect(component.modalPassword).toBe('');
    expect(component.passwordModalError).toBeNull();
    expect(component.selectedPageNumber).toBe(1);
    expect(component.parseState).toBe('IDLE');
    expect(component.parseResult).toBeNull();
    expect(component.reviewTransactions).toEqual([]);
    expect(component.validationSummary).toBeNull();
  });

  it('should trigger parsing successfully and transition to Step 3', () => {
    component.uploadResult = { fileId: 'FILE-P5-TEST' } as any;

    const mockParseResult = {
      isSuccess: true,
      bankName: 'HDFC Bank',
      parserVersion: 'HDFC-v1',
      totalExtractedTransactions: 2
    };

    const mockTxnsResponse = {
      items: [
        {
          id: 'TX-1',
          transactionDate: '2026-08-01T00:00:00',
          description: 'SALARY CREDIT',
          amount: 50000,
          credit: 50000,
          debit: null,
          balance: 75000,
          validationStatus: 'VALID',
          validationIssues: []
        }
      ],
      totalCount: 1,
      page: 1,
      pageSize: 50,
      totalPages: 1,
      summary: {
        totalTransactions: 1,
        validCount: 1,
        reviewCount: 0,
        invalidCount: 0,
        correctedCount: 0,
        balanceContinuity: 'BALANCED'
      }
    };

    mockStatementService.parseStatement.mockReturnValue(of(mockParseResult));
    mockStatementService.getTransactions.mockReturnValue(of(mockTxnsResponse));

    component.triggerParsing();

    expect(mockStatementService.parseStatement).toHaveBeenCalledWith('FILE-P5-TEST');
    expect(component.parseState).toBe('PARSED');
    expect(component.parseResult).toEqual(mockParseResult);
    expect(component.currentStep).toBe(3);
    expect(mockStatementService.getTransactions).toHaveBeenCalled();
    expect(component.reviewTransactions.length).toBe(1);
    expect(component.validationSummary?.validCount).toBe(1);
  });

  it('should handle parsing errors gracefully', () => {
    component.uploadResult = { fileId: 'FILE-P5-ERR' } as any;

    mockStatementService.parseStatement.mockReturnValue(
      throwError(() => ({ error: { detail: 'Unsupported bank statement format.' } }))
    );

    component.triggerParsing();

    expect(component.parseState).toBe('ERROR');
    expect(component.parseError).toBe('Unsupported bank statement format.');
  });

  it('should open edit modal and populate editForm with transaction data', () => {
    const txn: any = {
      id: 'TX-EDIT-1',
      transactionDate: '2026-08-10T12:00:00Z',
      valueDate: '2026-08-10T12:00:00Z',
      description: 'VENDOR INVOICE',
      reference: 'INV-902',
      debit: 1500,
      credit: null,
      amount: 1500,
      balance: 10000,
      transactionType: 'Debit',
      validationStatus: 'REVIEW'
    };

    component.openEditTransaction(txn);

    expect(component.isEditingTransaction).toBe(true);
    expect(component.editingTransaction).toBe(txn);
    expect(component.editForm.description).toBe('VENDOR INVOICE');
    expect(component.editForm.reference).toBe('INV-902');
    expect(component.editForm.debit).toBe(1500);
    expect(component.editForm.credit).toBeNull();
    expect(component.editForm.reason).toBe('');

    component.closeEditModal();
    expect(component.isEditingTransaction).toBe(false);
    expect(component.editingTransaction).toBeNull();
  });

  it('should enforce validation before submitting a correction', () => {
    component.uploadResult = { fileId: 'FILE-P5-CORR' } as any;
    component.editingTransaction = { id: 'TX-CORR-1' } as any;
    component.isEditingTransaction = true;

    // Missing reason
    component.editForm = {
      transactionDate: '2026-08-10',
      valueDate: '2026-08-10',
      description: 'TEST',
      reference: 'REF',
      debit: 100,
      credit: null,
      amount: 100,
      balance: 500,
      transactionType: 'Debit',
      reason: ''
    };
    component.submitCorrection();
    expect(component.editError).toContain('A reason for correction is required');

    // Missing description
    component.editForm.reason = 'Audit correction';
    component.editForm.description = '';
    component.submitCorrection();
    expect(component.editError).toContain('Description cannot be empty');

    // Both debit and credit
    component.editForm.description = 'Valid description';
    component.editForm.credit = 200;
    component.submitCorrection();
    expect(component.editError).toContain('Cannot enter both Debit and Credit');
  });

  it('should submit valid correction, update list and fetch updated summary', () => {
    component.uploadResult = { fileId: 'FILE-P5-CORR' } as any;
    const initialTxn: any = {
      id: 'TX-CORR-SUCCESS',
      transactionDate: '2026-08-10',
      description: 'ORIGINAL DESC',
      debit: 500,
      credit: null,
      amount: 500,
      balance: 2000,
      validationStatus: 'REVIEW'
    };
    component.reviewTransactions = [initialTxn];
    component.openEditTransaction(initialTxn);

    component.editForm.description = 'CORRECTED DESC';
    component.editForm.reason = 'Corrected typo per bank voucher';

    const correctedTxn: any = {
      ...initialTxn,
      description: 'CORRECTED DESC',
      validationStatus: 'CORRECTED',
      isCorrected: true
    };

    const updatedSummary: any = {
      totalTransactions: 1,
      validCount: 0,
      reviewCount: 0,
      invalidCount: 0,
      correctedCount: 1,
      balanceContinuity: 'BALANCED'
    };

    mockStatementService.correctTransaction.mockReturnValue(of(correctedTxn));
    mockStatementService.getValidationSummary.mockReturnValue(of(updatedSummary));

    component.submitCorrection();

    expect(mockStatementService.correctTransaction).toHaveBeenCalledWith(
      'FILE-P5-CORR',
      'TX-CORR-SUCCESS',
      expect.objectContaining({
        description: 'CORRECTED DESC',
        reason: 'Corrected typo per bank voucher'
      })
    );

    expect(component.isEditingTransaction).toBe(false);
    expect(component.reviewTransactions[0].description).toBe('CORRECTED DESC');
    expect(component.reviewTransactions[0].validationStatus).toBe('CORRECTED');
    expect(mockStatementService.getValidationSummary).toHaveBeenCalledWith('FILE-P5-CORR');
    expect(component.validationSummary).toEqual(updatedSummary);
  });

  it('should trigger excel export and download when statement is valid', () => {
    component.uploadResult = { fileId: 'FILE-EXCEL-TEST' } as any;
    component.validationSummary = {
      invalidCount: 0,
      bankName: 'HDFC Bank'
    } as any;

    const dummyBlob = new Blob(['sample excel content'], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' });
    mockStatementService.exportExcel.mockReturnValue(of(dummyBlob));

    component.downloadExcel();

    expect(mockStatementService.exportExcel).toHaveBeenCalledWith('FILE-EXCEL-TEST');
    expect(component.isExportingExcel).toBe(false);
    expect(component.exportError).toBeNull();
    expect(component.successNotice).toContain('exported successfully');
  });

  it('should block excel export when statement contains invalid transactions', () => {
    component.uploadResult = { fileId: 'FILE-INVALID-TEST' } as any;
    component.validationSummary = {
      invalidCount: 3,
      bankName: 'Axis Bank'
    } as any;

    component.downloadExcel();

    expect(mockStatementService.exportExcel).not.toHaveBeenCalled();
    expect(component.exportError).toContain('contains 3 invalid transaction(s)');
  });

  it('should handle export error gracefully', () => {
    component.uploadResult = { fileId: 'FILE-ERR-TEST' } as any;
    component.validationSummary = { invalidCount: 0 } as any;

    mockStatementService.exportExcel.mockReturnValue(
      throwError(() => ({ error: { detail: 'Server failed to generate spreadsheet.' } }))
    );

    component.downloadExcel();

    expect(component.isExportingExcel).toBe(false);
    expect(component.exportError).toBe('Server failed to generate spreadsheet.');
  });

  // =========================================================================
  // Phase 8: Workflow Stepper & Contextual State Tests
  // =========================================================================
  it('should accurately compute 5-stage stepper statuses across pipeline states', () => {
    // Initial IDLE state
    expect(component.step1Status).toBe('active');
    expect(component.step2Status).toBe('pending');
    expect(component.step3Status).toBe('pending');
    expect(component.step4Status).toBe('pending');
    expect(component.step5Status).toBe('pending');

    // Upload error state
    component.uploadState = 'ERROR';
    expect(component.step1Status).toBe('error');

    // Uploaded state
    component.uploadState = 'UPLOADED';
    component.uploadResult = { fileId: 'FILE-STEP-TEST' } as any;
    expect(component.step1Status).toBe('completed');
    expect(component.step2Status).toBe('active');

    // Processing / Extraction state
    component.extractionState = 'EXTRACTING';
    expect(component.step2Status).toBe('active');

    // Extraction error
    component.extractionState = 'ERROR';
    expect(component.step2Status).toBe('error');

    // Parsed state with clean validation
    component.extractionState = 'EXTRACTED';
    component.parseState = 'PARSED';
    component.validationSummary = {
      totalTransactions: 25,
      validCount: 25,
      reviewCount: 0,
      invalidCount: 0,
      correctedCount: 0,
      bankName: 'HDFC Bank',
      isReadyForExport: true
    } as any;
    component.currentStep = 3;

    expect(component.step2Status).toBe('completed');
    expect(component.step3Status).toBe('completed');
    expect(component.step4Status).toBe('active');
    expect(component.step5Status).toBe('pending');
    expect(component.isExportBlocked).toBe(false);
    expect(component.validationHealthStatus).toBe('ready');
    expect(component.detectedBankName).toBe('HDFC Bank');
    expect(component.totalTransactionCount).toBe(25);

    // Validation with invalid transactions (blocking export)
    component.validationSummary!.invalidCount = 2;
    component.validationSummary!.isReadyForExport = false;
    expect(component.step3Status).toBe('error');
    expect(component.step4Status).toBe('error');
    expect(component.step5Status).toBe('error');
    expect(component.isExportBlocked).toBe(true);
    expect(component.validationHealthStatus).toBe('blocked');

    // Excel exported successfully
    component.validationSummary!.invalidCount = 0;
    component.successNotice = 'Excel workbook exported successfully.';
    expect(component.step5Status).toBe('completed');
  });

  it('should navigate with onStepperClick only when stage prerequisites are satisfied', () => {
    // In IDLE: only step 1 is accessible
    component.currentStep = 1;
    component.onStepperClick(2);
    expect(component.currentStep).toBe(1);
    component.onStepperClick(3);
    expect(component.currentStep).toBe(1);
    component.onStepperClick(5);
    expect(component.currentStep).toBe(1);

    // When file uploaded: step 2 becomes accessible
    component.uploadResult = { fileId: 'TEST-FILE' } as any;
    component.onStepperClick(2);
    expect(component.currentStep).toBe(2);

    // When transactions parsed: step 3 and 5 become accessible
    component.parseState = 'PARSED';
    component.reviewTransactions = [{ id: 'TXN-1' } as any];
    component.onStepperClick(3);
    expect(component.currentStep).toBe(3);

    component.onStepperClick(5);
    expect(component.currentStep).toBe(4);

    // Can always return to step 1
    component.onStepperClick(1);
    expect(component.currentStep).toBe(1);
  });

  it('should verify sample review data banner and upload-information badge row are completely removed', () => {
    component.currentStep = 1;
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.sample-test-bar')).toBeNull();
    expect(compiled.textContent).not.toContain('Need to test the 9-column transaction ledger right away?');
    expect(compiled.textContent).not.toContain('Load Sample Review Data');
    expect(compiled.textContent).not.toContain('Max 50 MB');
    expect(compiled.textContent).not.toContain('50 MB');

    // Verify upload information badge row is completely removed
    expect(compiled.querySelector('.drop-badges-row')).toBeNull();
    expect(compiled.querySelector('.spec-pill')).toBeNull();
    expect(compiled.textContent).not.toContain('PDF Document');
    expect(compiled.textContent).not.toContain('In-Memory Security');
    expect(compiled.textContent).not.toContain('SHA-256 Verified');

    // Verify Choose PDF button and dropzone remain intact
    expect(compiled.querySelector('.dropzone')).not.toBeNull();
    expect(compiled.querySelector('.btn-file')).not.toBeNull();

    expect((component as any).loadSampleData).toBeUndefined();
    expect((component as any).sampleTransactions).toBeUndefined();
    expect((component as any).transactions).toBeUndefined();
  });

  it('should cancel in-flight upload, unsubscribe from stream, and return state to FILE_SELECTED', () => {
    const file = new File(['%PDF-1.4 test'], 'statement.pdf', { type: 'application/pdf' });
    component.handleFileSelection(file);
    mockStatementService.uploadStatement.mockReturnValue(NEVER);

    component.startUpload();
    expect(component.uploadState).toBe('UPLOADING');
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const cancelBtn = compiled.querySelector('.uploading-actions-right button');
    expect(cancelBtn).not.toBeNull();

    component.cancelUpload();
    expect(component.uploadState).toBe('FILE_SELECTED');
    expect(component.uploadProgress).toBe(0);
    expect(component.selectedFile).toBe(file);
  });

  // =========================================================================
  // Phase 6: Background Processing & Job System Tests
  // =========================================================================
  it('should automatically track background job when upload completes', () => {
    const validFile = new File(['%PDF-1.4 test'], 'august_statement.pdf', { type: 'application/pdf' });
    component.handleFileSelection(validFile);

    const mockResponse: StatementUploadResponse = {
      success: true,
      message: 'Bank statement uploaded successfully.',
      fileId: 'JOB-P6-TEST-1',
      jobId: 'JOB-P6-TEST-1',
      originalFileName: 'august_statement.pdf',
      storedFileName: 'JOB-P6-TEST-1.pdf',
      fileSizeBytes: 1024,
      fileSizeFormatted: '1.00 KB',
      contentType: 'application/pdf',
      uploadedAt: new Date().toISOString(),
      status: 'Queued',
      processingStatus: 0,
      isDuplicate: false,
      fileHash: 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
      clientId: 'TENANT-1',
      financialYearId: 'FY-1'
    };

    mockStatementService.uploadStatement.mockReturnValue(of(new HttpResponse({ body: mockResponse, status: 200 })));
    mockStatementService.getJobStatus.mockReturnValue(of({
      jobId: 'JOB-P6-TEST-1',
      fileId: 'JOB-P6-TEST-1',
      fileName: 'august_statement.pdf',
      status: 'Processing',
      statusCode: 1,
      stage: 'Extracting PDF layout geometry',
      progressPercent: 35,
      isTerminal: false,
      requiresPassword: false
    }));

    component.startUpload();

    expect(component.activeJobId).toBe('JOB-P6-TEST-1');
    expect(mockStatementService.getJobStatus).toHaveBeenCalledWith('JOB-P6-TEST-1');
    expect(component.jobStatus?.status).toBe('Processing');
    expect(component.jobStatus?.stage).toBe('Extracting PDF layout geometry');
  });

  it('should cancel active job cleanly when user requests cancellation', () => {
    component.activeJobId = 'JOB-P6-CANCEL-1';
    component.jobStatus = {
      jobId: 'JOB-P6-CANCEL-1',
      fileId: 'JOB-P6-CANCEL-1',
      fileName: 'august_statement.pdf',
      status: 'Processing',
      statusCode: 1,
      stage: 'Processing PDF',
      progressPercent: 40,
      isTerminal: false,
      requiresPassword: false
    };

    mockStatementService.cancelJob.mockReturnValue(of({ message: 'Cancelled', jobId: 'JOB-P6-CANCEL-1' }));

    component.cancelActiveJob();

    expect(mockStatementService.cancelJob).toHaveBeenCalledWith('JOB-P6-CANCEL-1');
    expect(component.jobStatus?.status).toBe('Cancelled');
    expect(component.jobStatus?.isTerminal).toBe(true);
  });

  it('should retry a failed job and re-enter tracking pipeline', () => {
    component.activeJobId = 'JOB-P6-RETRY-1';
    component.jobStatus = {
      jobId: 'JOB-P6-RETRY-1',
      fileId: 'JOB-P6-RETRY-1',
      fileName: 'august_statement.pdf',
      status: 'Failed',
      statusCode: 3,
      stage: 'Failed',
      progressPercent: 100,
      isTerminal: true,
      requiresPassword: false,
      errorMessage: 'Network glitch'
    };

    mockStatementService.retryJob.mockReturnValue(of({
      jobId: 'JOB-P6-RETRY-1',
      fileId: 'JOB-P6-RETRY-1',
      fileName: 'august_statement.pdf',
      status: 'Queued',
      statusCode: 0,
      stage: 'Re-enqueued in pool',
      progressPercent: 5,
      isTerminal: false,
      requiresPassword: false
    }));

    component.retryActiveJob();

    expect(mockStatementService.retryJob).toHaveBeenCalledWith('JOB-P6-RETRY-1');
    expect(component.activeJobId).toBe('JOB-P6-RETRY-1');
  });

  it('should unlock password-protected background job and resume polling', () => {
    component.activeJobId = 'JOB-P6-PWD-1';
    component.modalPassword = 'SecretPassword123';

    mockStatementService.unlockJob.mockReturnValue(of({
      jobId: 'JOB-P6-PWD-1',
      fileId: 'JOB-P6-PWD-1',
      fileName: 'protected.pdf',
      status: 'Queued',
      statusCode: 0,
      stage: 'Unlocked and enqueued for processing',
      progressPercent: 15,
      isTerminal: false,
      requiresPassword: false
    }));

    component.submitPasswordModal();

    expect(mockStatementService.unlockJob).toHaveBeenCalledWith('JOB-P6-PWD-1', 'SecretPassword123');
    expect(component.showPasswordModal).toBe(false);
    expect(component.modalPassword).toBe('');
    expect(component.statementPassword).toBe('');
  });

  // =========================================================================
  // Phase 3: Universal Statement Engine (Review -> Approve -> Convert -> Learn)
  // =========================================================================
  describe('Phase 3: Universal Review & Approval Flow', () => {
    const mockUniversalReview: UniversalReviewDto = {
      fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
      fileName: 'unknown_bank_sample.pdf',
      status: 'ReviewRequired',
      detectedBank: 'Federal Bank Format',
      confidence: {
        headerConfidence: 0.9,
        columnConfidence: 0.85,
        dataContinuityConfidence: 0.8,
        overallScore: 0.82,
        level: 'Medium',
        reviewReasons: ['Minor balance gap on row 2']
      },
      financialValidation: {
        totalRowsChecked: 2,
        reconciledRowsCount: 1,
        failedRowsCount: 1,
        missingBalanceCount: 0,
        missingAmountCount: 0,
        totalDebits: 500,
        totalCredits: 5000,
        openingBalance: 10000,
        closingBalance: 14500,
        isFullyReconciled: false,
        reconciliationRate: 0.5,
        discrepancies: ['Row 2: Balance mismatch detected']
      },
      columns: [
        { columnIndex: 0, columnType: 'Date', headerText: 'Txn Date', leftX: 40, rightX: 100, confidence: 0.9 },
        { columnIndex: 1, columnType: 'Description', headerText: 'Particulars', leftX: 100, rightX: 280, confidence: 0.85 },
        { columnIndex: 2, columnType: 'Reference', headerText: 'Ref No', leftX: 280, rightX: 350, confidence: 0.8 },
        { columnIndex: 3, columnType: 'Debit', headerText: 'Withdrawals', leftX: 350, rightX: 420, confidence: 0.85 },
        { columnIndex: 4, columnType: 'Credit', headerText: 'Deposits', leftX: 420, rightX: 490, confidence: 0.85 },
        { columnIndex: 5, columnType: 'Balance', headerText: 'Balance', leftX: 490, rightX: 580, confidence: 0.9 }
      ],
      transactions: [
        {
          id: 'cand-001',
          rowNumber: 1,
          pageNumber: 1,
          date: '2026-04-01T00:00:00Z',
          description: 'UPI / CLIENT SERVICES PAYMENT',
          reference: 'REF98765',
          debit: 500,
          credit: null,
          amount: 500,
          balance: 9500,
          direction: 'Debit',
          isDateAmbiguous: false,
          isDirectionAmbiguous: false,
          isBalanceMismatch: false,
          validationWarnings: [],
          validationErrors: [],
          isUserEdited: false
        },
        {
          id: 'cand-002',
          rowNumber: 2,
          pageNumber: 1,
          date: '2026-04-02T00:00:00Z',
          description: 'SALARY / VENDOR CREDIT',
          reference: 'REF98766',
          debit: null,
          credit: 5000,
          amount: 5000,
          balance: 14500,
          direction: 'Credit',
          isDateAmbiguous: false,
          isDirectionAmbiguous: false,
          isBalanceMismatch: true,
          validationWarnings: ['Row 2: Balance mismatch detected from previous balance'],
          validationErrors: [],
          isUserEdited: false
        }
      ],
      warnings: ['Row 2: Balance mismatch detected from previous balance'],
      rowsRequiringAttention: [2],
      isApprovalRequired: true,
      isConversionAllowed: true,
      totalTransactions: 2,
      attentionCount: 1,
      updatedAt: '2026-10-04T20:00:00Z'
    };

    it('1. should render review screen when job status reaches ReviewRequired', () => {
      mockStatementService.getUniversalReview.mockReturnValue(of(mockUniversalReview));

      component.uploadResult = {
        success: true,
        message: 'Uploaded',
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        originalFileName: 'unknown_bank_sample.pdf',
        storedFileName: 'stored_sample.pdf',
        fileSizeBytes: 2048,
        fileSizeFormatted: '2 KB',
        contentType: 'application/pdf',
        uploadedAt: '2026-10-04T20:00:00Z',
        status: 'Processing',
        processingStatus: 1,
        isDuplicate: false,
        fileHash: 'abcdef',
        clientId: 'TENANT-1',
        financialYearId: 'FY-1'
      };
      component.jobStatus = {
        jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        fileName: 'unknown_bank_sample.pdf',
        status: 'ReviewRequired',
        statusCode: 6,
        stage: 'ReviewRequired',
        progressPercent: 90,
        isTerminal: true,
        requiresPassword: false
      };

      component.loadUniversalReview('9A7E688C-841D-4C64-9D0C-8B988CCD63F8');
      fixture.detectChanges();

      expect(component.isUniversalReview).toBe(true);
      expect(component.universalReview).toEqual(mockUniversalReview);
      expect(component.currentStep).toBe(3);

      const workbench = fixture.nativeElement.querySelector('.universal-workbench-container');
      expect(workbench).toBeTruthy();

      const title = fixture.nativeElement.querySelector('.universal-title');
      expect(title.textContent).toContain('Federal Bank Format');
    });

    it('2. should display candidate transactions with amounts and confidence metrics', () => {
      mockStatementService.getUniversalReview.mockReturnValue(of(mockUniversalReview));
      component.loadUniversalReview('9A7E688C-841D-4C64-9D0C-8B988CCD63F8');
      fixture.detectChanges();

      const rows = fixture.nativeElement.querySelectorAll('.financial-table tbody tr');
      expect(rows.length).toBe(2);

      const firstRowDesc = rows[0].querySelector('.narration-main');
      expect(firstRowDesc.textContent).toContain('UPI / CLIENT SERVICES PAYMENT');

      const kpis = fixture.nativeElement.querySelectorAll('.universal-kpi-box');
      expect(kpis.length).toBeGreaterThanOrEqual(3);
    });

    it('3. should display warnings and attention badges on problematic rows', () => {
      mockStatementService.getUniversalReview.mockReturnValue(of(mockUniversalReview));
      component.loadUniversalReview('9A7E688C-841D-4C64-9D0C-8B988CCD63F8');
      fixture.detectChanges();

      const attentionPills = fixture.nativeElement.querySelectorAll('.badge-warning');
      expect(attentionPills.length).toBeGreaterThan(0);

      const warningItems = fixture.nativeElement.querySelectorAll('.warning-list li');
      expect(warningItems.length).toBeGreaterThan(0);
      expect(warningItems[0].textContent).toContain('Balance mismatch detected');
    });

    it('4. should open candidate edit modal and allow user to correct values', () => {
      component.isUniversalReview = true;
      component.universalReview = { ...mockUniversalReview };
      component.uploadResult = {
        success: true,
        message: 'Uploaded',
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        originalFileName: 'unknown_bank_sample.pdf',
        storedFileName: 'stored_sample.pdf',
        fileSizeBytes: 2048,
        fileSizeFormatted: '2 KB',
        contentType: 'application/pdf',
        uploadedAt: '2026-10-04T20:00:00Z',
        status: 'Processing',
        processingStatus: 1,
        isDuplicate: false,
        fileHash: 'abcdef',
        clientId: 'TENANT-1',
        financialYearId: 'FY-1'
      };
      component.currentStep = 3;
      fixture.detectChanges();

      const targetCandidate = mockUniversalReview.transactions[1];
      component.openEditCandidate(targetCandidate);
      fixture.detectChanges();

      expect(component.isEditingCandidate).toBe(true);
      expect(component.editingCandidate?.id).toBe('cand-002');
      expect(component.candidateEditForm.description).toBe('SALARY / VENDOR CREDIT');

      // Update form
      component.candidateEditForm.description = 'CORRECTED SALARY CREDIT';
      component.candidateEditForm.credit = 5000;
      component.candidateEditForm.debit = null;

      const updatedReview: UniversalReviewDto = {
        ...mockUniversalReview,
        transactions: [
          mockUniversalReview.transactions[0],
          {
            ...targetCandidate,
            description: 'CORRECTED SALARY CREDIT',
            isUserEdited: true,
            isBalanceMismatch: false,
            validationWarnings: []
          }
        ]
      };

      mockStatementService.correctUniversalTransaction.mockReturnValue(of(updatedReview));

      component.saveCandidateEdit();

      expect(mockStatementService.correctUniversalTransaction).toHaveBeenCalledWith(
        '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        'cand-002',
        expect.objectContaining({
          description: 'CORRECTED SALARY CREDIT',
          credit: 5000
        })
      );
      expect(component.isEditingCandidate).toBe(false);
      expect(component.universalReview?.transactions[1].description).toBe('CORRECTED SALARY CREDIT');
    });

    it('5. should reject simultaneous Debit and Credit in candidate editing', () => {
      component.isUniversalReview = true;
      component.universalReview = { ...mockUniversalReview };
      component.uploadResult = {
        success: true,
        message: 'Uploaded',
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        originalFileName: 'unknown_bank_sample.pdf',
        storedFileName: 'stored_sample.pdf',
        fileSizeBytes: 2048,
        fileSizeFormatted: '2 KB',
        contentType: 'application/pdf',
        uploadedAt: '2026-10-04T20:00:00Z',
        status: 'Processing',
        processingStatus: 1,
        isDuplicate: false,
        fileHash: 'abcdef',
        clientId: 'TENANT-1',
        financialYearId: 'FY-1'
      };
      const targetCandidate = mockUniversalReview.transactions[0];
      component.openEditCandidate(targetCandidate);

      component.candidateEditForm.debit = 1000;
      component.candidateEditForm.credit = 2000;

      component.saveCandidateEdit();

      expect(component.candidateEditError).toContain('cannot be entered simultaneously');
      expect(mockStatementService.correctUniversalTransaction).not.toHaveBeenCalled();
    });

    it('6. should execute Approve & Convert and transition to approved state', () => {
      mockStatementService.approveUniversalReview.mockReturnValue(of({
        success: true,
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        convertedCount: 2,
        learnedFingerprintHash: 'FINGERPRINT-HASH-12345',
        message: 'Successfully approved and persisted 2 transactions.'
      }));

      component.uploadResult = {
        success: true,
        message: 'Uploaded',
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        originalFileName: 'unknown_bank_sample.pdf',
        storedFileName: 'stored_sample.pdf',
        fileSizeBytes: 2048,
        fileSizeFormatted: '2 KB',
        contentType: 'application/pdf',
        uploadedAt: '2026-10-04T20:00:00Z',
        status: 'Processing',
        processingStatus: 1,
        isDuplicate: false,
        fileHash: 'abcdef',
        clientId: 'TENANT-1',
        financialYearId: 'FY-1'
      };
      component.isUniversalReview = true;
      component.universalReview = mockUniversalReview;
      component.currentStep = 3;
      fixture.detectChanges();

      component.approveAndConvert();

      expect(component.isApprovingReview).toBe(false);
      expect(mockStatementService.approveUniversalReview).toHaveBeenCalledWith('9A7E688C-841D-4C64-9D0C-8B988CCD63F8');
      expect(component.isUniversalReview).toBe(false);
      expect(component.successNotice).toContain('Statement approved!');
    });

    it('7. should handle approval API error gracefully without transitioning to success', () => {
      mockStatementService.approveUniversalReview.mockReturnValue(
        throwError(() => ({ error: { detail: 'Statement has 1 unresolved critical balance conflict.' } }))
      );

      component.uploadResult = {
        success: true,
        message: 'Uploaded',
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        originalFileName: 'unknown_bank_sample.pdf',
        storedFileName: 'stored_sample.pdf',
        fileSizeBytes: 2048,
        fileSizeFormatted: '2 KB',
        contentType: 'application/pdf',
        uploadedAt: '2026-10-04T20:00:00Z',
        status: 'Processing',
        processingStatus: 1,
        isDuplicate: false,
        fileHash: 'abcdef',
        clientId: 'TENANT-1',
        financialYearId: 'FY-1'
      };
      component.isUniversalReview = true;
      component.universalReview = mockUniversalReview;

      component.approveAndConvert();

      expect(component.isApprovingReview).toBe(false);
      expect(component.isUniversalReview).toBe(true);
      expect(component.approvalError).toContain('Statement has 1 unresolved critical balance conflict.');
    });

    it('8. should show loading indicator while approval is in flight', () => {
      mockStatementService.approveUniversalReview.mockReturnValue(NEVER);

      component.uploadResult = {
        success: true,
        message: 'Uploaded',
        fileId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        jobId: '9A7E688C-841D-4C64-9D0C-8B988CCD63F8',
        originalFileName: 'unknown_bank_sample.pdf',
        storedFileName: 'stored_sample.pdf',
        fileSizeBytes: 2048,
        fileSizeFormatted: '2 KB',
        contentType: 'application/pdf',
        uploadedAt: '2026-10-04T20:00:00Z',
        status: 'Processing',
        processingStatus: 1,
        isDuplicate: false,
        fileHash: 'abcdef',
        clientId: 'TENANT-1',
        financialYearId: 'FY-1'
      };
      component.isUniversalReview = true;
      component.universalReview = mockUniversalReview;
      component.currentStep = 3;

      component.approveAndConvert();
      expect(component.isApprovingReview).toBe(true);
    });
  });
});
