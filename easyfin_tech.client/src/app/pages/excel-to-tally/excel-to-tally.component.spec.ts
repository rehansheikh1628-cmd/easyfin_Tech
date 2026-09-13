import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormsModule } from '@angular/forms';
import { of, throwError } from 'rxjs';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { ExcelToTallyComponent } from './excel-to-tally.component';
import { ExcelToTallyService, ExcelValidationResult } from '../../services/excel-to-tally.service';

describe('ExcelToTallyComponent', () => {
  let component: ExcelToTallyComponent;
  let fixture: ComponentFixture<ExcelToTallyComponent>;
  let mockExcelService: {
    downloadTemplate: ReturnType<typeof vi.fn>;
    validateExcelFile: ReturnType<typeof vi.fn>;
    generateTallyXml: ReturnType<typeof vi.fn>;
    saveBlob: ReturnType<typeof vi.fn>;
  };

  const mockValidResult: ExcelValidationResult = {
    success: true,
    totalRows: 2,
    validRows: 2,
    warningRows: 0,
    invalidRows: 0,
    sampleRowsSkipped: 0,
    hasOfficialTemplateSignature: true,
    isReadyForXmlGeneration: true,
    validationMessages: [],
    parsedTransactions: [
      {
        rowNumber: 2,
        date: '2025-04-01T00:00:00',
        narration: 'Office Supplies',
        chequeRefNo: 'REF-001',
        valueDate: '2025-04-01T00:00:00',
        drAmount: 1500,
        crAmount: null,
        closingBalance: 48500,
        ledgerName: 'Stationery',
        bankName: 'HDFC Bank',
        status: 'Valid',
        issues: [],
        isDebit: true,
        isCredit: false,
        amount: 1500
      },
      {
        rowNumber: 3,
        date: '2025-04-02T00:00:00',
        narration: 'Client Retainer',
        chequeRefNo: 'REF-002',
        valueDate: '2025-04-02T00:00:00',
        drAmount: null,
        crAmount: 25000,
        closingBalance: 73500,
        ledgerName: 'Consulting',
        bankName: 'HDFC Bank',
        status: 'Valid',
        issues: [],
        isDebit: false,
        isCredit: true,
        amount: 25000
      }
    ],
    templateInfo: 'EasyFin Template'
  };

  beforeEach(async () => {
    mockExcelService = {
      downloadTemplate: vi.fn(),
      validateExcelFile: vi.fn(),
      generateTallyXml: vi.fn(),
      saveBlob: vi.fn()
    };

    await TestBed.configureTestingModule({
      declarations: [ExcelToTallyComponent],
      imports: [FormsModule],
      providers: [{ provide: ExcelToTallyService, useValue: mockExcelService }]
    }).compileComponents();

    fixture = TestBed.createComponent(ExcelToTallyComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('1. should create component with default initial state', () => {
    expect(component).toBeTruthy();
    expect(component.selectedFile).toBeNull();
    expect(component.validationResult).toBeNull();
    expect(component.activeFilter).toBe('ALL');
    expect(component.searchTerm).toBe('');
    expect(component.isValidating).toBe(false);
  });

  it('2. should reject non-Excel file extension', () => {
    const file = new File(['dummy'], 'test.pdf', { type: 'application/pdf' });
    component.handleFile(file);

    expect(component.selectedFile).toBeNull();
    expect(component.errorMessage).toContain("Invalid file format '.pdf'");
  });

  it('3. should reject file larger than 50MB', () => {
    const largeFile = new File([''], 'large.xlsx');
    Object.defineProperty(largeFile, 'size', { value: 55 * 1024 * 1024 });

    component.handleFile(largeFile);

    expect(component.selectedFile).toBeNull();
    expect(component.errorMessage).toContain('File size exceeds the 50MB limit');
  });

  it('4. should process valid Excel file and receive validation result', () => {
    const file = new File(['dummy content'], 'transactions.xlsx', {
      type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
    });
    mockExcelService.validateExcelFile.mockReturnValue(of(mockValidResult));

    component.handleFile(file);

    expect(component.selectedFile).toBe(file);
    expect(mockExcelService.validateExcelFile).toHaveBeenCalledWith(file);
    expect(component.validationResult).toEqual(mockValidResult);
    expect(component.validationResult?.isReadyForXmlGeneration).toBe(true);
    expect(component.filteredTransactions.length).toBe(2);
  });

  it('5. successful validation response clears loading state and renders results', () => {
    const file = new File(['dummy content'], 'transactions.xlsx', {
      type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
    });
    mockExcelService.validateExcelFile.mockReturnValue(of(mockValidResult));

    component.handleFile(file);
    fixture.detectChanges();

    // Verify loading state is cleared
    expect(component.isValidating).toBe(false);

    // Verify DOM renders the summary and table instead of upload overlay
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.validation-overlay')).toBeNull();
    expect(compiled.querySelector('.results-container')).not.toBeNull();
    expect(compiled.querySelector('.summary-card .card-value')?.textContent).toContain('2');
    expect(compiled.querySelectorAll('.preview-table tbody tr').length).toBe(2);
  });

  it('6. HTTP error clears loading state and does not leave UI permanently loading', () => {
    const file = new File(['dummy content'], 'error.xlsx');
    mockExcelService.validateExcelFile.mockReturnValue(
      throwError(() => new Error('Server connection lost'))
    );

    component.handleFile(file);
    fixture.detectChanges();

    expect(component.isValidating).toBe(false);
    expect(component.errorMessage).toBe('Server connection lost');

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.validation-overlay')).toBeNull();
    expect(compiled.querySelector('.alert-error')).not.toBeNull();
  });

  it('7. malformed or structured 400 error response clears loading state gracefully', () => {
    const file = new File(['dummy content'], 'bad_headers.xlsx');
    mockExcelService.validateExcelFile.mockReturnValue(
      throwError(() => ({
        error: {
          success: false,
          errorMessage: 'Missing required column headers: Bank Name',
          isReadyForXmlGeneration: false,
          validationMessages: [
            { rowNumber: null, column: 'Headers', severity: 2, message: 'Missing required column headers: Bank Name' }
          ]
        }
      }))
    );

    component.handleFile(file);
    fixture.detectChanges();

    expect(component.isValidating).toBe(false);
    expect(component.errorMessage).toBe('Missing required column headers: Bank Name');
    expect(component.validationResult).not.toBeNull();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.validation-overlay')).toBeNull();
    expect(compiled.querySelector('.alert-error')?.textContent).toContain('Bank Name');
  });

  it('8. should download template and trigger saveBlob', () => {
    const blob = new Blob(['template data'], { type: 'application/vnd.ms-excel.sheet.macroEnabled.12' });
    mockExcelService.downloadTemplate.mockReturnValue(of(blob));

    component.downloadTemplate();

    expect(mockExcelService.downloadTemplate).toHaveBeenCalled();
    expect(mockExcelService.saveBlob).toHaveBeenCalledWith(blob, 'EasyFin_Tally_Import_Template_v1.xlsm');
    expect(component.isDownloadingTemplate).toBe(false);
    expect(component.successMessage).toContain('downloaded successfully');
  });

  it('9. should filter transactions by tab and search query', () => {
    component.validationResult = {
      ...mockValidResult,
      totalRows: 3,
      parsedTransactions: [
        ...mockValidResult.parsedTransactions,
        {
          rowNumber: 4,
          date: '2025-04-03T00:00:00',
          narration: 'Invalid Row Example',
          status: 'Invalid',
          issues: [],
          isDebit: true,
          isCredit: false,
          amount: 500
        }
      ]
    };

    // Filter by tab
    component.setFilter('VALID');
    expect(component.filteredTransactions.length).toBe(2);

    component.setFilter('INVALID');
    expect(component.filteredTransactions.length).toBe(1);

    component.setFilter('ALL');
    expect(component.filteredTransactions.length).toBe(3);

    // Filter by search query
    component.searchTerm = 'Stationery';
    expect(component.filteredTransactions.length).toBe(1);
    expect(component.filteredTransactions[0].narration).toBe('Office Supplies');
  });

  it('10. should reset state when reset is invoked', () => {
    component.validationResult = mockValidResult;
    component.selectedFile = new File([''], 'file.xlsx');
    component.searchTerm = 'query';

    component.reset();

    expect(component.validationResult).toBeNull();
    expect(component.selectedFile).toBeNull();
    expect(component.searchTerm).toBe('');
    expect(component.activeFilter).toBe('ALL');
    expect(component.isValidating).toBe(false);
    expect(component.isGeneratingXml).toBe(false);
    expect(component.xmlSuccessMessage).toBeNull();
    expect(component.xmlErrorMessage).toBeNull();
  });

  it('11. should call generateTallyXml on button click and trigger saveBlob', () => {
    component.validationResult = mockValidResult;
    const xmlBlob = new Blob(['<ENVELOPE></ENVELOPE>'], { type: 'application/xml' });
    mockExcelService.generateTallyXml.mockReturnValue(of(xmlBlob));

    component.generateTallyXml();

    expect(mockExcelService.generateTallyXml).toHaveBeenCalledWith(mockValidResult);
    expect(mockExcelService.saveBlob).toHaveBeenCalled();
    expect(mockExcelService.saveBlob.mock.calls[0][0]).toBe(xmlBlob);
    expect(mockExcelService.saveBlob.mock.calls[0][1]).toContain('EasyFin_Tally_Export_');
    expect(component.isGeneratingXml).toBe(false);
    expect(component.xmlSuccessMessage).toBe('Tally XML generated successfully.');
  });

  it('12. should prevent duplicate calls when isGeneratingXml is true', () => {
    component.validationResult = mockValidResult;
    component.isGeneratingXml = true;

    component.generateTallyXml();

    expect(mockExcelService.generateTallyXml).not.toHaveBeenCalled();
  });

  it('13. should handle XML generation error from API and display clear error message', async () => {
    component.validationResult = mockValidResult;
    const errorJson = JSON.stringify({ message: 'XML generation blocked: Row 27 — Dr Amount and Cr Amount cannot both be populated.' });
    const errorBlob = new Blob([errorJson], { type: 'application/json' });

    mockExcelService.generateTallyXml.mockReturnValue(
      throwError(() => ({
        error: errorBlob
      }))
    );

    component.generateTallyXml();
    fixture.detectChanges();

    // Wait a tick for blob.text() promise
    await new Promise((r) => setTimeout(r, 50));
    fixture.detectChanges();

    expect(component.isGeneratingXml).toBe(false);
    expect(component.xmlErrorMessage).toContain('Row 27');
  });

  it('14. should disable button when validation is not ready', () => {
    const invalidResult: ExcelValidationResult = {
      ...mockValidResult,
      isReadyForXmlGeneration: false,
      invalidRows: 1
    };
    mockExcelService.validateExcelFile.mockReturnValue(of(invalidResult));
    component.handleFile(new File(['dummy'], 'transactions.xlsx'));
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const btn = compiled.querySelector('#generateXmlBtn') as HTMLButtonElement;
    expect(btn).not.toBeNull();
    expect(btn.disabled).toBe(true);
  });

  it('15. should compute correct stepper states across workflow lifecycle', () => {
    // Initial state
    expect(component.step1Status).toBe('active');
    expect(component.step2Status).toBe('pending');
    expect(component.step3Status).toBe('pending');
    expect(component.step4Status).toBe('pending');
    expect(component.step5Status).toBe('pending');
    expect(component.step6Status).toBe('pending');

    // After template download
    component.templateDownloaded = true;
    expect(component.step1Status).toBe('completed');
    expect(component.step2Status).toBe('active');

    // After file selected & valid result
    component.validationResult = mockValidResult;
    expect(component.step1Status).toBe('completed');
    expect(component.step2Status).toBe('completed');
    expect(component.step3Status).toBe('completed');
    expect(component.step4Status).toBe('completed');
    expect(component.step5Status).toBe('active');
    expect(component.step6Status).toBe('pending');

    // After XML generated
    component.xmlSuccessMessage = 'Generated';
    expect(component.step5Status).toBe('completed');
    expect(component.step6Status).toBe('completed');
  });

  it('16. should format file sizes accurately', () => {
    expect(component.formatFileSize(0)).toBe('0 B');
    expect(component.formatFileSize(500)).toBe('500 B');
    expect(component.formatFileSize(2048)).toBe('2.0 KB');
    expect(component.formatFileSize(5 * 1024 * 1024)).toBe('5.00 MB');
  });

  it('17. should format dates safely without throwing exceptions', () => {
    expect(component.formatDate(null)).toBe('—');
    expect(component.formatDate('')).toBe('—');
    expect(component.formatDate('25/01/2026')).toBe('25/01/2026');
    expect(component.formatDate('2026-08-15T00:00:00')).toBe('15/08/2026');
    expect(component.formatDate('InvalidDateString')).toBe('InvalidDateString');
  });

  it('18. should compute appropriate fileStatusBadge', () => {
    expect(component.fileStatusBadge.text).toBe('Uploaded');

    component.isValidating = true;
    expect(component.fileStatusBadge.text).toBe('Validating');

    component.isValidating = false;
    component.validationResult = mockValidResult;
    expect(component.fileStatusBadge.text).toBe('Ready for XML');

    component.xmlSuccessMessage = 'XML ready';
    expect(component.fileStatusBadge.text).toBe('XML Ready');
  });
});
