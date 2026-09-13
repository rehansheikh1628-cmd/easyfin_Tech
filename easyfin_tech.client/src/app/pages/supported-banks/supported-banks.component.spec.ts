import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NO_ERRORS_SCHEMA, provideZonelessChangeDetection } from '@angular/core';
import { RouterModule } from '@angular/router';
import { By } from '@angular/platform-browser';
import { Observable, of } from 'rxjs';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { SupportedBanksComponent } from './supported-banks.component';
import { AuthService } from '../../services/auth.service';

describe('SupportedBanksComponent (Phase 13 UI Redesign)', () => {
  let component: SupportedBanksComponent;
  let fixture: ComponentFixture<SupportedBanksComponent>;
  let mockAuthService: {
    isAuthenticated: boolean;
    currentUser$: Observable<unknown>;
  };

  beforeEach(async () => {
    mockAuthService = {
      isAuthenticated: true,
      currentUser$: of(null)
    };

    await TestBed.configureTestingModule({
      declarations: [SupportedBanksComponent],
      imports: [CommonModule, FormsModule, RouterModule.forRoot([])],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AuthService, useValue: mockAuthService }
      ],
      schemas: [NO_ERRORS_SCHEMA]
    }).compileComponents();

    fixture = TestBed.createComponent(SupportedBanksComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('1. should create component with default initial state', () => {
    expect(component).toBeTruthy();
    expect(component.searchQuery).toBe('');
    expect(component.selectedCategory).toBe('ALL');
    expect(component.totalBanksCount).toBe(8);
    expect(component.totalFormatsCount).toBe(9);
  });

  it('2. should contain all 8 authoritative bank institutions from backend source code', () => {
    const bankIds = component.supportedBanks.map(b => b.id);
    expect(bankIds).toEqual(['hdfc', 'icici', 'axis', 'kotak', 'yes', 'sbi', 'boi', 'central']);

    const bankNames = component.supportedBanks.map(b => b.bankName);
    expect(bankNames).toContain('HDFC Bank');
    expect(bankNames).toContain('ICICI Bank');
    expect(bankNames).toContain('Axis Bank');
    expect(bankNames).toContain('Kotak Mahindra Bank');
    expect(bankNames).toContain('YES BANK');
    expect(bankNames).toContain('State Bank of India');
    expect(bankNames).toContain('Bank of India');
    expect(bankNames).toContain('Central Bank of India');
  });

  it('3. should verify ICICI Bank has exactly 2 validated format profiles (v1 and v2)', () => {
    const icici = component.supportedBanks.find(b => b.id === 'icici');
    expect(icici).toBeDefined();
    expect(icici?.supportedFormats.length).toBe(2);
    expect(icici?.supportedFormats[0].formatId).toBe('ICICI-v1');
    expect(icici?.supportedFormats[1].formatId).toBe('ICICI-v2');
  });

  it('4. should filter banks by sector category (Private Sector vs Public Sector)', () => {
    // Private Sector
    component.setCategory('Private Sector');
    expect(component.filteredBanks.length).toBe(5);
    expect(component.filteredBanks.every(b => b.category === 'Private Sector')).toBe(true);

    // Public Sector
    component.setCategory('Public Sector');
    expect(component.filteredBanks.length).toBe(3);
    expect(component.filteredBanks.every(b => b.category === 'Public Sector')).toBe(true);

    // All
    component.setCategory('ALL');
    expect(component.filteredBanks.length).toBe(8);
  });

  it('5. should search banks by bank name', () => {
    component.searchQuery = 'HDFC';
    expect(component.filteredBanks.length).toBe(1);
    expect(component.filteredBanks[0].id).toBe('hdfc');

    component.searchQuery = 'State Bank';
    expect(component.filteredBanks.length).toBe(1);
    expect(component.filteredBanks[0].id).toBe('sbi');
  });

  it('6. should search banks by parser version code', () => {
    component.searchQuery = 'AXIS-v1';
    expect(component.filteredBanks.length).toBe(1);
    expect(component.filteredBanks[0].id).toBe('axis');

    component.searchQuery = 'ICICI-v2';
    expect(component.filteredBanks.length).toBe(1);
    expect(component.filteredBanks[0].id).toBe('icici');
  });

  it('7. should search banks by short code', () => {
    component.searchQuery = 'CBI';
    expect(component.filteredBanks.length).toBe(1);
    expect(component.filteredBanks[0].id).toBe('central');

    component.searchQuery = 'BOI';
    expect(component.filteredBanks.length).toBe(1);
    expect(component.filteredBanks[0].id).toBe('boi');
  });

  it('8. should search banks by layout keyword (e.g. Overdraft)', () => {
    component.searchQuery = 'Overdraft';
    expect(component.filteredBanks.length).toBe(1);
    expect(component.filteredBanks[0].id).toBe('icici');
  });

  it('9. should return empty array when search query matches no banks', () => {
    component.searchQuery = 'Citibank International';
    expect(component.filteredBanks.length).toBe(0);
  });

  it('10. should clear search and reset category when clearSearch is called', () => {
    component.searchQuery = 'Kotak';
    component.selectedCategory = 'Private Sector';

    component.clearSearch();
    expect(component.searchQuery).toBe('');
    expect(component.selectedCategory).toBe('ALL');
    expect(component.filteredBanks.length).toBe(8);
  });

  it('11. should render the new 4-item horizontal feature strip in DOM and ensure old metrics strip is absent', () => {
    const featureStrip = fixture.debugElement.query(By.css('.banks-features-strip'));
    expect(featureStrip).toBeTruthy();

    const items = fixture.debugElement.queryAll(By.css('.feature-strip-item'));
    expect(items.length).toBe(4);

    const stripText = featureStrip.nativeElement.textContent;
    expect(stripText).toContain('BANK STATEMENT → EXCEL');
    expect(stripText).toContain('9+ CALIBRATED PROFILES');
    expect(stripText).toContain('VALIDATED OUTPUT');
    expect(stripText).toContain('TALLY PRIME READY');

    // Verify old statistics cards are absent
    const metricsStrip = fixture.debugElement.query(By.css('.summary-metrics-strip'));
    expect(metricsStrip).toBeNull();
    expect(component.totalBanksCount).toBe(8);
    expect(component.totalFormatsCount).toBe(9);
  });

  it('12. should render all 8 bank cards in the DOM by default', () => {
    const cards = fixture.debugElement.queryAll(By.css('.bank-profile-card'));
    expect(cards.length).toBe(8);
  });

  it('13. should render OCR guidance callout and digital PDF warning', () => {
    const ocrCard = fixture.debugElement.query(By.css('#ocrAdvisoryCard'));
    expect(ocrCard).toBeTruthy();
    expect(ocrCard.nativeElement.textContent).toContain('Scanned statements, photocopies');
    expect(ocrCard.nativeElement.textContent).toContain('not currently included in the standard digital-PDF parser workflow');
  });

  it('14. should render workflow clarification distinguishing from Excel to Tally XML', () => {
    const boundaryText = fixture.debugElement.query(By.css('.boundary-text'));
    expect(boundaryText).toBeTruthy();
    expect(boundaryText.nativeElement.textContent).toContain('Bank Statement PDF → Excel Converter');
    expect(boundaryText.nativeElement.textContent).toContain('Excel → Tally XML');
  });

  it('15. should render search empty state when filtered results are 0', () => {
    component.searchQuery = 'UnknownNonExistentBank';
    fixture.detectChanges();

    const emptyState = fixture.debugElement.query(By.css('#searchEmptyState'));
    expect(emptyState).toBeTruthy();
    expect(emptyState.nativeElement.textContent).toContain('No supported banks match your search');

    // Click reset search button
    const resetBtn = fixture.debugElement.query(By.css('#resetSearchBtn'));
    expect(resetBtn).toBeTruthy();
    resetBtn.nativeElement.click();
    fixture.detectChanges();

    expect(component.searchQuery).toBe('');
    expect(component.filteredBanks.length).toBe(8);
  });

  it('16. should provide direct CTA link to /converter', () => {
    const ctaBtn = fixture.debugElement.query(By.css('#openConverterBtn'));
    expect(ctaBtn).toBeTruthy();
    expect(ctaBtn.attributes['routerLink']).toBe('/converter');
  });
});
