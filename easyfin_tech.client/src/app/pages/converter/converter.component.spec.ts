import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ConverterComponent } from './converter.component';
import { StatementService, StatementUploadResponse } from '../../services/statement.service';
import { of, from, throwError } from 'rxjs';
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
  };

  beforeEach(async () => {
    mockStatementService = {
      uploadStatement: vi.fn(),
      formatBytes: (bytes: number) => `${bytes} B`,
      extractStatement: vi.fn(),
      getStatementExtraction: vi.fn(),
      parseStatement: vi.fn(),
      getTransactions: vi.fn(),
      getTransactionById: vi.fn(),
      correctTransaction: vi.fn(),
      getValidationSummary: vi.fn(),
      revalidateStatement: vi.fn(),
      exportExcel: vi.fn()
    };

    await TestBed.configureTestingModule({
      declarations: [ConverterComponent],
      imports: [FormsModule, RouterModule.forRoot([])],
      providers: [
        { provide: StatementService, useValue: mockStatementService }
      ]
    }).compileComponents();
  });

  beforeEach(() => {
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

  it('should reject a file larger than 50MB and set state to ERROR', () => {
    const oversizedFile = new File(['data'], 'giant.pdf', { type: 'application/pdf' });
    Object.defineProperty(oversizedFile, 'size', { value: 55 * 1024 * 1024 });

    component.handleFileSelection(oversizedFile);

    expect(component.uploadState).toBe('ERROR');
    expect(component.selectedFile).toBeNull();
    expect(component.uploadError).toContain('larger than the allowed upload size');
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

  it('should handle PasswordProtected status properly', () => {
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

    expect(component.extractionState).toBe('PASSWORD_REQUIRED');
    expect(component.extractionError).toContain('Document is encrypted and requires a password.');
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

  it('should reset state completely when resetUpload is called', () => {
    component.selectedFile = new File(['%PDF'], 'test.pdf');
    component.uploadState = 'UPLOADED';
    component.uploadProgress = 100;
    component.uploadError = 'some error';
    component.extractionState = 'EXTRACTED';
    component.extractionResult = { pageCount: 1 } as any;
    component.statementPassword = 'secret';

    component.resetUpload();

    expect(component.selectedFile).toBeNull();
    expect(component.uploadState).toBe('IDLE');
    expect(component.uploadProgress).toBe(0);
    expect(component.uploadError).toBeNull();
    expect(component.uploadResult).toBeNull();
    expect(component.extractionState).toBe('IDLE');
    expect(component.extractionResult).toBeNull();
    expect(component.statementPassword).toBe('');
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
});
